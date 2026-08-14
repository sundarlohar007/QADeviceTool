using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

/// <summary>
/// Device details and per-device actions.
/// </summary>
public partial class DeviceViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly IScrcpyService _scrcpyService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly ISessionService _sessionService;
    private readonly IUiDispatcher _dispatcher;

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

    [ObservableProperty]
    private string _deviceNotes = string.Empty;

    [ObservableProperty]
    private string _deviceTag = string.Empty;

    public DeviceViewModel(
        IAdbService adbService,
        IIosService iosService,
        IScrcpyService scrcpyService,
        IDeviceMonitorService deviceMonitor,
        ISessionService sessionService, IUiDispatcher? dispatcher = null)
    {
        _adbService = adbService;
        _iosService = iosService;
        _scrcpyService = scrcpyService;
        _deviceMonitor = deviceMonitor;
        _sessionService = sessionService;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
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
            LoadDevicePreferences(value.Serial);
        }
    }

    private void LoadDevicePreferences(string serial)
    {
        var pref = PreferencesService.GetDevicePreference(serial);
        DeviceNotes = pref.Notes;
        DeviceTag = pref.Tag;
    }

    [RelayCommand]
    private void SaveDeviceNotes()
    {
        if (SelectedDevice == null) return;

        var pref = PreferencesService.GetDevicePreference(SelectedDevice.Serial);
        pref.Notes = DeviceNotes;
        pref.Tag = DeviceTag;
        PreferencesService.SaveDevicePreference(SelectedDevice.Serial, pref);
        StatusMessage = "Device notes saved.";
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
            : $"Failed to start mirroring: {_scrcpyService.LastError ?? "Unknown error"}";
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

        var outputDir = PreferencesService.Current.SessionsRootDirectory;
        if (!System.IO.Directory.Exists(outputDir)) System.IO.Directory.CreateDirectory(outputDir);

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


    // ─── Wireless ADB ────────────────────────────────────────────
    [ObservableProperty]
    private string _wirelessIpAddress = string.Empty;

    [RelayCommand]
    private async Task EnableWirelessAsync()
    {
        if (SelectedDevice == null)
        {
            StatusMessage = "[!] No device selected.";
            return;
        }

        if (SelectedDevice.Platform != DevicePlatform.Android)
        {
            StatusMessage = "Wireless mode is only available for Android devices.";
            return;
        }

        // Security warning: tcpip opens the device to all machines on the network
        var confirm = UiServices.Dialogs.Confirm(
            "Security Warning — Wireless ADB",
            "Enabling wireless ADB will open your device to TCP connections on port 5555. Any machine on the same network can connect to and control this device. Are you sure you want to continue?");
        if (!confirm)
        {
            StatusMessage = "Wireless ADB cancelled.";
            return;
        }
        StatusMessage = "Enabling wireless ADB mode...";

        try
        {
            var result = await _adbService.EnableWirelessAsync(SelectedDevice.Serial);
            if (result.Success)
            {
                // If result.Message is an IP address, auto-fill it
                if (System.Text.RegularExpressions.Regex.IsMatch(result.Message, @"^\d+\.\d+\.\d+\.\d+$"))
                {
                    WirelessIpAddress = result.Message;
                    StatusMessage = $"TCP mode enabled. Device IP: {result.Message}. You can unplug USB and click Connect.";
                }
                else
                {
                    StatusMessage = result.Message;
                }
            }
            else
            {
                StatusMessage = $"[!] {result.Message}";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Device] EnableWirelessAsync failed");
            StatusMessage = $"[!] Wireless error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ConnectWirelessAsync()
    {
        if (string.IsNullOrWhiteSpace(WirelessIpAddress))
        {
            StatusMessage = "[!] Enter the device IP address first.";
            return;
        }

        StatusMessage = $"Connecting to {WirelessIpAddress}...";

        try
        {
            var result = await _adbService.ConnectWirelessAsync(WirelessIpAddress.Trim());
            StatusMessage = result.Success ? result.Message : $"[!] {result.Message}";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Device] ConnectWirelessAsync failed");
            StatusMessage = $"[!] Connection error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectWirelessAsync()
    {
        if (string.IsNullOrWhiteSpace(WirelessIpAddress))
        {
            StatusMessage = "[!] Enter the device IP address first.";
            return;
        }

        try
        {
            var result = await _adbService.DisconnectWirelessAsync(WirelessIpAddress.Trim());
            StatusMessage = result.Success ? "Disconnected." : $"[!] {result.Message}";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Device] DisconnectWirelessAsync failed");
            StatusMessage = $"[!] Disconnect error: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        GC.SuppressFinalize(this);
    }
}

