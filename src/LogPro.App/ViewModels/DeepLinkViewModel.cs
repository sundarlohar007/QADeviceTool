using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

public partial class DeepLinkViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly IDeviceMonitorService _deviceMonitor;
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

    public DeepLinkViewModel(IAdbService adbService, IIosService iosService, IDeviceMonitorService deviceMonitor)
    {
        _adbService = adbService;
        _iosService = iosService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = Application.Current.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
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
            bool success;
            if (SelectedDevice.Platform == DevicePlatform.iOS)
            {
                // pymobiledevice3 has no first-class openurl command. Inform user explicitly.
                success = await _iosService.OpenUrlAsync(SelectedDevice.Serial, TargetUrl.Trim());
                StatusMessage = success
                    ? $"Successfully launched: {TargetUrl}"
                    : "[!] iOS deep links not supported via pymobiledevice3. Use Safari or a configurator.";
            }
            else
            {
                success = await _adbService.BroadcastIntentAsync(SelectedDevice.Serial, TargetUrl.Trim());
                StatusMessage = success
                    ? $"Successfully launched: {TargetUrl}"
                    : "[!] Failed to route intent. Verify the URL scheme, target app install state, and that the device is unlocked.";
            }
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

    public void Dispose()
    {
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        GC.SuppressFinalize(this);
    }
}

