using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;

namespace QADeviceTool.ViewModels;

/// <summary>
/// Device details and per-device actions.
/// </summary>
public partial class DeviceViewModel : ObservableObject
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly ScrcpyService _scrcpyService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _deviceDetails = "Select a device to view details.";

    [ObservableProperty]
    private bool _isMirroring;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public DeviceViewModel(
        AdbService adbService,
        IosService iosService,
        ScrcpyService scrcpyService,
        DeviceMonitorService deviceMonitor)
    {
        _adbService = adbService;
        _iosService = iosService;
        _scrcpyService = scrcpyService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = Application.Current.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Invoke(() =>
        {
            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);
        });
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value != null)
        {
            _ = LoadDeviceDetailsAsync(value);
        }
    }

    private async Task LoadDeviceDetailsAsync(DeviceInfo device)
    {
        DeviceDetails = "Loading device details...";

        try
        {
            DeviceInfo detailed;
            if (device.Platform == DevicePlatform.Android)
                detailed = await _adbService.GetDeviceDetailsAsync(device);
            else
                detailed = await _iosService.GetDeviceDetailsAsync(device);

            DeviceDetails = $"""
                {detailed.DisplayName}
                
                Model: {detailed.Model}
                Serial: {detailed.Serial}
                OS Version: {detailed.OsVersion}
                Battery: {detailed.BatteryLevel}
                Status: {detailed.StatusText}
                Platform: {detailed.Platform}
                """;
        }
        catch
        {
            DeviceDetails = "Failed to load device details.";
        }
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        await _deviceMonitor.PollDevicesAsync();
    }

    [RelayCommand]
    private async Task StartMirrorAsync()
    {
        if (SelectedDevice == null) return;

        if (SelectedDevice.Platform != DevicePlatform.Android)
        {
            StatusMessage = "Screen mirroring is only available for Android devices.";
            return;
        }

        StatusMessage = "Starting screen mirror...";
        var success = await _scrcpyService.StartMirroringAsync(SelectedDevice.Serial);
        IsMirroring = success;
        StatusMessage = success
            ? "Screen mirroring active"
            : "Failed to start mirroring. Is scrcpy installed?";
    }

    [RelayCommand]
    private void StopMirror()
    {
        _scrcpyService.StopMirroring();
        IsMirroring = false;
        StatusMessage = "Mirror stopped.";
    }

    [RelayCommand]
    private async Task TakeSnapshotAsync()
    {
        if (SelectedDevice == null) return;

        var outputDir = Helpers.PathHelper.GetDefaultSessionsDirectory();
        Helpers.PathHelper.EnsureSessionsDirectory();

        var fileName = $"snapshot_{SelectedDevice.Serial}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var outputPath = System.IO.Path.Combine(outputDir, fileName);

        StatusMessage = "Capturing screenshot...";

        bool success = SelectedDevice.Platform == DevicePlatform.Android
            ? await _adbService.CaptureScreenshotAsync(SelectedDevice.Serial, outputPath)
            : await _iosService.CaptureScreenshotAsync(SelectedDevice.Serial, outputPath);

        StatusMessage = success
            ? $"Snapshot saved: {fileName}"
            : "Failed to capture snapshot.";
    }
}
