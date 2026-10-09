using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Helpers;
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
    private readonly IPreferencesStore _preferences;
    private readonly Dictionary<(DevicePlatform Platform, string Serial), (string Notes, string Tag)> _drafts = new();
    private int _detailsGeneration;
    private string? _lastSnapshotDirectory;
    private bool _disposed;
    private bool _loadingPreferences;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _deviceDetails = "Select a device to view details.";

    public bool IsMirroring => SelectedDevice?.Platform == DevicePlatform.Android && _scrcpyService.IsRunning &&
        _scrcpyService.MirroredDeviceSerial == SelectedDevice.Serial;
    public bool HasSelectedDevice => SelectedDevice != null;
    public bool CanMirror => SelectedDevice?.Platform == DevicePlatform.Android &&
        SelectedDevice.ConnectionState == DeviceConnectionState.Online && !SelectedDevice.IsTemporarilyUnavailable && !IsMirroring;
    public bool CanTakeSnapshot => SelectedDevice?.ConnectionState == DeviceConnectionState.Online &&
        !SelectedDevice.IsTemporarilyUnavailable;
    public bool HasSnapshotDirectory => _lastSnapshotDirectory != null;
    public bool MaskSerials { get; private set; } = true;
    public string SerialVisibilityActionText => MaskSerials ? "Reveal selected serial" : "Mask selected serial";
    public string SelectedSerialDisplay => SelectedDevice == null ? string.Empty :
        MaskSerials ? SelectedDevice.MaskedSerial : SelectedDevice.Serial;
    public string CapabilityNotice => SelectedDevice switch
    {
        null => "Select a device to see available actions.",
        { ConnectionState: DeviceConnectionState.Unauthorized } => "Authorize this Android device and reconnect before using device actions.",
        { ConnectionState: DeviceConnectionState.PendingTrust } => "Trust this computer on the iOS device before using device actions.",
        { ConnectionState: not DeviceConnectionState.Online } => "Device is unavailable. Reconnect it before using device actions.",
        { IsTemporarilyUnavailable: true } => "Device missed a recent check. Waiting for it to reconnect before enabling actions.",
        { Platform: DevicePlatform.iOS } => "iOS: screenshots are available when Developer Mode permits them. scrcpy mirroring and ADB wireless are Android-only.",
        _ => SecurityHelper.OfflineOnly
            ? "Android: screenshots and scrcpy mirroring are available. Wireless ADB is disabled in this offline build."
            : "Android: screenshots, scrcpy mirroring, and wireless ADB are available."
    };

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _deviceNotes = string.Empty;

    [ObservableProperty]
    private string _deviceTag = string.Empty;

    [ObservableProperty]
    private string _discoveryMessage = string.Empty;

    public DeviceViewModel(
        IAdbService adbService,
        IIosService iosService,
        IScrcpyService scrcpyService,
        IDeviceMonitorService deviceMonitor,
        ISessionService sessionService, IUiDispatcher? dispatcher = null, IPreferencesStore? preferences = null, bool isActive = true)
    {
        _isActive = isActive;
        _adbService = adbService;
        _iosService = iosService;
        _scrcpyService = scrcpyService;
        _deviceMonitor = deviceMonitor;
        _sessionService = sessionService;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;
        _preferences = preferences ?? PreferencesService.Instance;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        _deviceMonitor.DiscoveryStatusChanged += OnDiscoveryStatusChanged;
        _scrcpyService.StateChanged += OnMirrorStateChanged;
        DiscoveryMessage = _deviceMonitor.LastDiscoveryError ?? string.Empty;
        OnDevicesChanged(_deviceMonitor.CurrentDevices.ToList());
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            var selected = SelectedDevice;
            for (var i = 0; i < devices.Count; i++)
            {
                if (i == Devices.Count) Devices.Add(devices[i]);
                else if (!SameDeviceData(Devices[i], devices[i])) Devices[i] = devices[i];
            }
            while (Devices.Count > devices.Count) Devices.RemoveAt(Devices.Count - 1);
            var replacement = devices.FirstOrDefault(d => selected != null && d.Serial == selected.Serial && d.Platform == selected.Platform);
            if (replacement == null && selected != null) SelectedDevice = null;
            else if (replacement != null && !ReferenceEquals(SelectedDevice, replacement)) SelectedDevice = replacement;
            NotifyDeviceState();
        });
    }

    private static bool SameDeviceData(DeviceInfo a, DeviceInfo b) =>
        a.Serial == b.Serial && a.Platform == b.Platform && a.ConnectionState == b.ConnectionState &&
        a.IsTemporarilyUnavailable == b.IsTemporarilyUnavailable &&
        a.Name == b.Name && a.Model == b.Model && a.OsVersion == b.OsVersion &&
        a.BatteryLevel == b.BatteryLevel && a.BatteryStatus == b.BatteryStatus &&
        a.Manufacturer == b.Manufacturer && a.Product == b.Product && a.UsbInfo == b.UsbInfo;

    private void OnDiscoveryStatusChanged(string? error) =>
        _dispatcher.Post(() => { if (!_disposed) DiscoveryMessage = error ?? string.Empty; });

    private void OnMirrorStateChanged() => _dispatcher.Post(() =>
    {
        if (_disposed) return;
        NotifyDeviceState();
        if (!_scrcpyService.IsRunning && StatusMessage.StartsWith("Screen mirroring active", StringComparison.Ordinal))
            StatusMessage = "Screen mirror closed.";
    });

    private void NotifyDeviceState()
    {
        OnPropertyChanged(nameof(IsMirroring));
        OnPropertyChanged(nameof(HasSelectedDevice));
        OnPropertyChanged(nameof(CanMirror));
        OnPropertyChanged(nameof(CanTakeSnapshot));
        OnPropertyChanged(nameof(CapabilityNotice));
        OnPropertyChanged(nameof(SelectedSerialDisplay));
    }

    private bool _isActive;
    private CancellationTokenSource? _detailsCts;
    public void SetActive(bool active)
    {
        _isActive = active;
        if (!active) { _detailsCts?.Cancel(); ++_detailsGeneration; _detailsKey = null; }
        else OnSelectedDeviceChanged(SelectedDevice);
    }
    private string? _detailsKey;
    private DateTime _detailsChecked;

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        NotifyDeviceState();
        if (value != null)
        {
            if (!value.IsReady)
            {
                _detailsCts?.Cancel();
                ++_detailsGeneration;
                _detailsKey = null;
                DeviceDetails = value.StatusText;
            }
            var key = $"{value.Platform}:{value.Serial}:{value.IsReady}:{value.OsVersion}";
            if (_isActive && value.IsReady && (_detailsKey != key || DateTime.UtcNow - _detailsChecked > TimeSpan.FromSeconds(60)))
            {
                _detailsKey = key;
                _detailsChecked = DateTime.UtcNow;
                _ = LoadDeviceDetailsAsync(value, ++_detailsGeneration);
            }
            LoadDevicePreferences(value.Platform, value.Serial);
        }
        else
        {
            ++_detailsGeneration;
            _detailsCts?.Cancel();
            _detailsKey = null;
            DeviceDetails = "Select a device to view details.";
            DeviceNotes = string.Empty;
            DeviceTag = string.Empty;
        }
    }

    private void LoadDevicePreferences(DevicePlatform platform, string serial)
    {
        _loadingPreferences = true;
        try
        {
            if (_drafts.TryGetValue((platform, serial), out var draft))
            {
                DeviceNotes = draft.Notes;
                DeviceTag = draft.Tag;
                return;
            }
            var pref = _preferences.GetDevicePreference(serial);
            DeviceNotes = pref.Notes;
            DeviceTag = pref.Tag;
        }
        finally { _loadingPreferences = false; }
    }

    partial void OnDeviceNotesChanged(string value) => CacheDraft();
    partial void OnDeviceTagChanged(string value) => CacheDraft();

    private void CacheDraft()
    {
        if (!_loadingPreferences && SelectedDevice != null)
            _drafts[(SelectedDevice.Platform, SelectedDevice.Serial)] = (DeviceNotes, DeviceTag);
    }

    [RelayCommand]
    private void SaveDeviceNotes()
    {
        if (SelectedDevice == null) return;

        var existing = _preferences.GetDevicePreference(SelectedDevice.Serial);
        var pref = new DevicePreference
        {
            Notes = DeviceNotes,
            Tag = DeviceTag,
            LastConnected = existing.LastConnected
        };
        StatusMessage = _preferences.TrySaveDevicePreference(SelectedDevice.Serial, pref)
            ? "Device notes and tag saved."
            : "Could not save device notes and tag. Check the application data directory.";
        if (StatusMessage.StartsWith("Device notes", StringComparison.Ordinal))
            _drafts.Remove((SelectedDevice.Platform, SelectedDevice.Serial));
    }

    private async Task LoadDeviceDetailsAsync(DeviceInfo device, int generation)
    {
        _detailsCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _detailsCts = cts;
        DeviceDetails = "Loading device details...";

        try
        {
            // Detail services enrich their input object; keep the live device list immutable.
            var lookup = device.WithTemporaryUnavailable(device.IsTemporarilyUnavailable);
            DeviceInfo detailed;
            if (device.Platform == DevicePlatform.Android)
                detailed = await DeviceQueries.DetailsAsync(_adbService, lookup, cts.Token);
            else
                detailed = await DeviceQueries.DetailsAsync(_iosService, lookup, cts.Token);

            if (cts.IsCancellationRequested || _disposed || generation != _detailsGeneration) return;
            DeviceDetails = $"""
                {detailed.DisplayName}
                
                Model: {detailed.Model}
                OS Version: {detailed.OsVersion}
                Battery: {detailed.BatteryLevel}
                Status: {detailed.StatusText}
                Platform: {detailed.Platform}
                """;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            AppLogger.Log.Warn(ex, "[Device] Could not load details");
            if (!_disposed && generation == _detailsGeneration)
                DeviceDetails = "Failed to load device details.";
        }
        finally { if (ReferenceEquals(_detailsCts, cts)) _detailsCts = null; }
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        StatusMessage = "Refreshing devices...";
        try
        {
            await _deviceMonitor.PollDevicesAsync();
            DiscoveryMessage = _deviceMonitor.LastDiscoveryError ?? string.Empty;
            StatusMessage = string.IsNullOrWhiteSpace(DiscoveryMessage)
                ? $"Refresh complete. {_deviceMonitor.CurrentDevices.Count} device(s) detected."
                : "Refresh completed with a discovery warning.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Device] Refresh failed");
            StatusMessage = "Device refresh failed. Check the connected tools and cables.";
        }
    }

    [RelayCommand]
    private async Task StartMirrorAsync()
    {
        var device = SelectedDevice;
        if (device == null) return;
        if (device.Platform != DevicePlatform.Android)
        {
            StatusMessage = "Screen mirroring is only available for Android devices.";
            return;
        }
        if (device.ConnectionState != DeviceConnectionState.Online || device.IsTemporarilyUnavailable)
        {
            StatusMessage = "Connect and authorize the Android device before mirroring.";
            return;
        }

        StatusMessage = "Starting screen mirror...";
        try
        {
            var success = await _scrcpyService.StartMirroringAsync(device.Serial);
            NotifyDeviceState();
            StatusMessage = success
                ? $"Screen mirroring active for {device.DisplayName}."
                : $"Failed to start mirroring: {_scrcpyService.LastError ?? "Unknown error"}";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Device] Mirror failed");
            StatusMessage = "Failed to start screen mirroring.";
        }
    }

    [RelayCommand]
    private void StopMirror()
    {
        if (!IsMirroring)
        {
            StatusMessage = "The selected device is not being mirrored.";
            return;
        }
        _scrcpyService.StopMirroring();
        NotifyDeviceState();
        StatusMessage = "Mirror stopped.";
    }

    [RelayCommand]
    private async Task TakeSnapshotAsync()
    {
        var device = SelectedDevice;
        if (device == null) return;
        if (device.ConnectionState != DeviceConnectionState.Online || device.IsTemporarilyUnavailable)
        {
            StatusMessage = "Connect and authorize the device before taking a snapshot.";
            return;
        }

        StatusMessage = "Capturing screenshot...";
        try
        {
            var outputDir = _sessionService.SessionsRootDirectory;
            if (!PathHelper.TryGetSafeLocalDirectory(outputDir, out var safeDirectory))
            {
                StatusMessage = "The snapshot folder is not a safe local directory.";
                return;
            }
            var fileName = $"snapshot_{SecurityHelper.HashSerial(device.Serial)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png";
            var outputPath = Path.Combine(safeDirectory, fileName);
            var iosResult = device.Platform == DevicePlatform.iOS
                ? await _iosService.CaptureScreenshotWithStatusAsync(device.Serial, outputPath)
                : default;
            var success = device.Platform == DevicePlatform.Android
                ? await _adbService.CaptureScreenshotAsync(device.Serial, outputPath)
                : iosResult.Success;
            success &= File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
            if (success)
            {
                _lastSnapshotDirectory = safeDirectory;
                OnPropertyChanged(nameof(HasSnapshotDirectory));
            }
            StatusMessage = success
                ? $"Snapshot saved: {fileName}"
                : device.Platform == DevicePlatform.iOS ? iosResult.Message : "Failed to capture a valid snapshot.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Device] Snapshot failed");
            StatusMessage = "Snapshot failed. Check the device and snapshot folder.";
        }
    }

    [RelayCommand]
    private void OpenSnapshotFolder()
    {
        if (_lastSnapshotDirectory == null || !Directory.Exists(_lastSnapshotDirectory)) return;
        try { Process.Start(new ProcessStartInfo(_lastSnapshotDirectory) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Device] Could not open snapshot folder");
            StatusMessage = "Could not open the snapshot folder.";
        }
    }

    [RelayCommand]
    private void ToggleSerialVisibility()
    {
        MaskSerials = !MaskSerials;
        OnPropertyChanged(nameof(MaskSerials));
        OnPropertyChanged(nameof(SelectedSerialDisplay));
        OnPropertyChanged(nameof(SerialVisibilityActionText));
    }


    // ─── Wireless ADB ────────────────────────────────────────────
    [ObservableProperty]
    private string _wirelessIpAddress = string.Empty;

    [RelayCommand]
    private async Task EnableWirelessAsync()
    {
        if (SecurityHelper.OfflineOnly)
        {
            StatusMessage = "Wireless ADB is unavailable in this offline build. Use USB.";
            return;
        }
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
        if (SecurityHelper.OfflineOnly)
        {
            StatusMessage = "Wireless ADB is unavailable in this offline build. Use USB.";
            return;
        }
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
        if (SecurityHelper.OfflineOnly)
        {
            StatusMessage = "Wireless ADB is unavailable in this offline build. Use USB.";
            return;
        }
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
        _detailsCts?.Cancel();
        _disposed = true;
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _deviceMonitor.DiscoveryStatusChanged -= OnDiscoveryStatusChanged;
        _scrcpyService.StateChanged -= OnMirrorStateChanged;
        GC.SuppressFinalize(this);
    }
}

