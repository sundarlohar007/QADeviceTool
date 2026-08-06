using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;
using LogPro.Helpers;

namespace LogPro.ViewModels;

public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly IIosService _iosService;
    private readonly IUiDispatcher _dispatcher;

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
    private readonly System.Text.StringBuilder _outputBuilder = new();

    public ShellViewModel(IDeviceMonitorService deviceMonitor, IIosService iosService)
    {
        _deviceMonitor = deviceMonitor;
        _iosService = iosService;
        _dispatcher = new WpfUiDispatcher(Application.Current.Dispatcher);

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
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
            var help = value.Platform == DevicePlatform.iOS
                ? "Type a pymobiledevice3 command (e.g. 'lockdown info', 'apps list', 'afc ls /', 'crash ls', 'diagnostics info'). iOS does not expose an interactive shell here."
                : $"Type an adb command (e.g. 'shell ls' or 'logcat -d'). 'adb -s {value.Serial}' is automatically prepended.";

            AppendOutput($"--- Selected Device: {value.DisplayName} ({value.Serial}) ---\n{help}\n");
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
                var passthrough = MapIosShellCommand(cmd);
                if (passthrough == null)
                {
                    AppendOutput("[iOS] Interactive shell not supported by pymobiledevice3.\n" +
                                 "Try: lockdown info | apps list | afc ls / | crash ls | diagnostics info | usbmux list");
                }
                else
                {
                    var udid = passthrough.StartsWith("usbmux", StringComparison.OrdinalIgnoreCase) || passthrough.StartsWith("version", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : SelectedDevice.Serial;
                    var result = await _iosService.ExecuteCommandAsync(udid, passthrough, 30000);
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
        _outputBuilder.Clear();
        ShellOutput = string.Empty;
        if (SelectedDevice != null)
        {
            AppendOutput($"--- Terminal Cleared ---\nTarget: {SelectedDevice.DisplayName} ({SelectedDevice.Serial})\n");
        }
    }

    private static string? MapIosShellCommand(string cmd)
    {
        var trimmed = cmd.Trim();
        var allowedPrefixes = new[]
        {
            "lockdown info", "lockdown get",
            "apps list", "apps query", "apps uninstall",
            "afc ls", "afc pull", "afc push",
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
                return trimmed;
            }
        }
        return null;
    }

    private void AppendOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _dispatcher.Post(() =>
        {
            _outputBuilder.Append(text.TrimEnd('\r', '\n')).Append('\n');
            if (_outputBuilder.Length > 50000)
            {
                _outputBuilder.Remove(0, _outputBuilder.Length - 25000);
            }
            ShellOutput = _outputBuilder.ToString();
        });
    }

    public void Dispose()
    {
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        GC.SuppressFinalize(this);
    }
}

