using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;
using QADeviceTool.Helpers;

namespace QADeviceTool.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly DeviceMonitorService _deviceMonitor;
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

    public ShellViewModel(DeviceMonitorService deviceMonitor)
    {
        _deviceMonitor = deviceMonitor;
        _dispatcher = Application.Current.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Invoke(() =>
        {
            var currentSelected = SelectedDevice?.Serial;
            
            Devices.Clear();
            foreach (var d in devices)
            {
                if (d.Platform == DevicePlatform.Android) // Shell only supported for Android currently
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

        var cmd = CommandInput.Trim();
        CommandInput = string.Empty; // Clear immediately for next input

        AppendOutput($"\n> {cmd}");
        IsExecuting = true;

        try
        {
            var adbPath = PathHelper.FindInPath("adb") ?? "adb";
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
