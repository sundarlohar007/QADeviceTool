using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;
using LogPro.Helpers;

namespace LogPro.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly IosService _iosService;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _commandInput = string.Empty;

    [ObservableProperty]
    private string _shellOutput = string.Empty;

    [ObservableProperty]
    private bool _isExecuting;

    public ShellViewModel(DeviceMonitorService deviceMonitor, IosService iosService)
    {
        _deviceMonitor = deviceMonitor;
        _iosService = iosService;
        _dispatcher = Application.Current.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var currentSelected = SelectedDevice?.Serial;

            Devices.Clear();
            foreach (var d in devices)
            {
                if (d.ConnectionState == DeviceConnectionState.Online)
                {
                    Devices.Add(d);
                }
            }

            if (!string.IsNullOrEmpty(currentSelected))
            {
                SelectedDevice = Devices.FirstOrDefault(d => d.Serial == currentSelected);
            }
            if (SelectedDevice == null && Devices.Count > 0)
            {
                SelectedDevice = Devices.First();
            }
        });
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        if (device.ConnectionState == DeviceConnectionState.Online)
        {
            SelectedDevice = device;
        }
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value != null)
        {
            AppendOutput($"--- Selected Device: {value.DisplayName} ({value.Serial}) ---\n" +
                         $"Type a command (e.g. 'shell ls' or 'logcat -d'). 'adb -s {value.Serial}' is automatically prepended.\n");
        }
        else
        {
            ShellOutput = string.Empty;
        }
        CommandInput = string.Empty;
    }

    [RelayCommand]
    private async Task ExecuteCommandAsync()
    {
        if (SelectedDevice == null || string.IsNullOrWhiteSpace(CommandInput)) return;

        if (SelectedDevice.ConnectionState != DeviceConnectionState.Online)
        {
            AppendOutput($"[Error] Device is {SelectedDevice.ConnectionState}. Cannot execute shell commands.\n");
            return;
        }

        var cmd = CommandInput.Trim();
        CommandInput = string.Empty;

        AppendOutput($"\n> {cmd}");
        IsExecuting = true;

        try
        {
            if (SelectedDevice.Platform == DevicePlatform.iOS)
            {
                // pymobiledevice3 has no non-interactive shell pipe (`developer shell` is an
                // IPython REPL). Map a small set of useful subcommands to RunAsync so the user
                // can still inspect lockdown/diagnostics/apps/crash without an interactive shell.
                var passthrough = MapIosShellCommand(SelectedDevice.Serial, cmd);
                if (passthrough == null)
                {
                    AppendOutput("[iOS] Interactive shell not supported by pymobiledevice3.\n" +
                                 "Try: lockdown info | apps list | crash ls | diagnostics info | usbmux list");
                }
                else
                {
                    var pyExe = ResolveSystemPython() ?? "python";
                    var result = await ToolLauncher.RunAsync(pyExe, $"-m pymobiledevice3 --no-color {passthrough}", 30000);
                    AppendOutput(string.IsNullOrWhiteSpace(result.Output) ? result.Error ?? "(no output)" : result.Output);
                }
            }
            else
            {
                var adbPath = ToolResolver.Resolve("adb");
                var result = await ToolLauncher.RunAsync(adbPath, $"-s {SelectedDevice.Serial} {cmd}", 60000);

            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                AppendOutput(result.Output);
            }
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                AppendOutput($"[Error]\n{result.Error}");
            }
            
                if (!result.Success && string.IsNullOrWhiteSpace(result.Error) && string.IsNullOrWhiteSpace(result.Output))
                {
                    AppendOutput($"[Command exited with code {result.ExitCode}]");
                }
            }
        }
        catch (Exception ex)
        {
            AppendOutput($"[Exception]\n{ex.Message}");
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private void ClearOutput()
    {
        ShellOutput = string.Empty;
        if (SelectedDevice != null)
        {
            AppendOutput($"--- Terminal Cleared ---\nTarget: {SelectedDevice.DisplayName} ({SelectedDevice.Serial})\n");
        }
    }

    private static string? MapIosShellCommand(string udid, string cmd)
    {
        var trimmed = cmd.Trim();
        var allowedPrefixes = new[]
        {
            "lockdown info", "lockdown get",
            "apps list", "apps query", "apps uninstall",
            "crash ls", "crash pull",
            "diagnostics info", "diagnostics mg",
            "usbmux list", "usbmux forward",
            "syslog live", "processes",
            "version"
        };
        foreach (var p in allowedPrefixes)
        {
            if (trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                var udidFlag = string.IsNullOrEmpty(udid) || trimmed.StartsWith("usbmux", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("version", StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $" --udid \"{udid}\"";
                return $"{trimmed}{udidFlag}";
            }
        }
        return null;
    }

    private static string? ResolveSystemPython()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathVar.Split(';')
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => System.IO.Path.Combine(p, "python.exe"))
            .FirstOrDefault(System.IO.File.Exists);
    }

    private void AppendOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _dispatcher.Invoke(() =>
        {
            if (ShellOutput.Length > 50000) // Keep it from growing infinitely
            {
                ShellOutput = ShellOutput.Substring(ShellOutput.Length - 25000);
            }
            ShellOutput += text.TrimEnd('\r', '\n') + "\n";
        });
    }
}
