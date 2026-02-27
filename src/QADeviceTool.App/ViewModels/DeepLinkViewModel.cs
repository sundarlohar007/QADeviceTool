using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;

namespace QADeviceTool.ViewModels;

public partial class DeepLinkViewModel : ObservableObject
{
    private readonly AdbService _adbService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _targetUrl = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isRouting;

    public DeepLinkViewModel(AdbService adbService, DeviceMonitorService deviceMonitor)
    {
        _adbService = adbService;
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
                if (d.Platform == DevicePlatform.Android) // Intents via CLI are reliable mostly on Android
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

    partial void OnTargetUrlChanged(string value)
    {
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private async Task FireIntentAsync()
    {
        if (SelectedDevice == null)
        {
            StatusMessage = "[!] No device selected.";
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetUrl))
        {
            StatusMessage = "[!] Please enter a valid URL or Intent URI.";
            return;
        }

        IsRouting = true;
        StatusMessage = $"Sending intent to {SelectedDevice.DisplayName}...";

        try
        {
            var success = await _adbService.BroadcastIntentAsync(SelectedDevice.Serial, TargetUrl.Trim());
            StatusMessage = success 
                ? $"Successfully launched: {TargetUrl}" 
                : $"[!] Failed to route intent. Check device status.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"[Error] {ex.Message}";
        }
        finally
        {
            IsRouting = false;
        }
    }
}
