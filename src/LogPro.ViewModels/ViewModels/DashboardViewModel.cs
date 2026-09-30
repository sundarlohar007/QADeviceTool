using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

/// <summary>
/// Dashboard — overview of devices, tool statuses, and quick actions.
/// </summary>
public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly IScrcpyService _scrcpyService;
    private readonly ISessionService _sessionService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly IDeviceStore _deviceStore;
    private readonly DependencyChecker _dependencyChecker;
    private readonly IUiDispatcher _dispatcher;
    private readonly SemaphoreSlim _toolStatusGate = new(1, 1);
    private bool _updatingDevices;
    private int _disposed;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private ObservableCollection<ToolStatus> _toolStatuses = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _welcomeMessage = "Welcome to LogPro";

    [ObservableProperty]
    private int _activeSessionCount;

    [ObservableProperty]
    private int _onlineDeviceCount;

    [ObservableProperty]
    private int _devicesNeedingAttentionCount;

    [ObservableProperty]
    private string _discoveryMessage = string.Empty;

    [ObservableProperty]
    private bool _isRefreshingDevices;

    [ObservableProperty]
    private string _toolCheckError = string.Empty;

    public ObservableCollection<string> RecentActivity { get; } = new();
    public bool HasRecentActivity => RecentActivity.Count > 0;
    public bool HasDiscoveryError => !string.IsNullOrWhiteSpace(DiscoveryMessage);
    public string DeviceCountSubtitle => HasDiscoveryError ? "Last known state; discovery failed" : "Online iOS & Android";
    public bool CanUseSelectedDevice => SelectedDevice?.ConnectionState == DeviceConnectionState.Online &&
        !SelectedDevice.IsTemporarilyUnavailable;
    public bool CanMirrorSelectedDevice => CanUseSelectedDevice && SelectedDevice?.Platform == DevicePlatform.Android;
    public bool HasPlatformNotice => SelectedDevice?.Platform == DevicePlatform.iOS;
    public string PlatformNotice => HasPlatformNotice
        ? "iOS screen mirroring is unavailable here. Snapshots and log capture are supported when the device is trusted."
        : string.Empty;
    public string MirrorActionText => _scrcpyService.IsRunning && _scrcpyService.MirroredDeviceSerial == SelectedDevice?.Serial
        ? "Stop Mirroring" : "Mirror Screen";
    public string ToolHealthSummary => !string.IsNullOrEmpty(ToolCheckError)
        ? "Check failed" : ToolStatuses.Count == 0
            ? "Checking…" : $"{ToolStatuses.Count(s => s.IsInstalled)}/{ToolStatuses.Count} ready";

    [ObservableProperty]
    private string _targetPackageName = string.Empty;

    [ObservableProperty]
    private string _scrcpyBitRate = "2M";

    [ObservableProperty]
    private string _scrcpyMaxFps = "60";

    [ObservableProperty]
    private string _scrcpyWindowPreset = "Default";

    [ObservableProperty]
    private string _pairingIpPort = string.Empty;

    [ObservableProperty]
    private string _pairingCode = string.Empty;

    [ObservableProperty]
    private string _discoveredPorts = string.Empty;

    [ObservableProperty]
    private string _wirelessStatus = SecurityHelper.OfflineOnly
        ? "[!] Wireless ADB is unavailable in this offline build. Connect Android devices by USB."
        : string.Empty;

    public DashboardViewModel(
        IAdbService adbService,
        IIosService iosService,
        IScrcpyService scrcpyService,
        ISessionService sessionService,
        IDeviceMonitorService deviceMonitor,
        DependencyChecker dependencyChecker, IDeviceStore deviceStore, IUiDispatcher? dispatcher = null)
    {
        _adbService = adbService;
        _iosService = iosService;
        _scrcpyService = scrcpyService;
        _sessionService = sessionService;
        _deviceMonitor = deviceMonitor;
        _deviceStore = deviceStore;
        _dependencyChecker = dependencyChecker;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        _deviceMonitor.DeviceConnected += OnDeviceConnected;
        _deviceMonitor.DeviceDisconnected += OnDeviceDisconnected;
        _deviceMonitor.DiscoveryStatusChanged += OnDiscoveryStatusChanged;
        _scrcpyService.StateChanged += OnMirrorStateChanged;
        _sessionService.CaptureStarted += OnCaptureStarted;
        _sessionService.CaptureStopped += OnCaptureStopped;
        ActiveSessionCount = _sessionService.ActiveSessions.Count;
        DiscoveryMessage = _deviceMonitor.LastDiscoveryError ?? string.Empty;
        OnDevicesChanged(_deviceMonitor.CurrentDevices.ToList());

        // Load initial data exclusively on a background thread so we don't block the UI rendering during startup
        Task.Run(async () =>
        {
            try
            {
                var keyword = PreferencesService.Current.TargetPackageName;
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    _dispatcher.Post(() => TargetPackageName = keyword);
                }
            }
            catch (Exception) { /* keyword load best-effort */ }


            try
            {
                await LoadToolStatusesAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Log.Debug(ex, "[Dashboard] LoadToolStatusesAsync failed on init");
            }
        });
    }

    partial void OnTargetPackageNameChanged(string value)
    {
        var target = value?.Trim() ?? string.Empty;
        if (PreferencesService.Current.TargetPackageName == target) return;
        PreferencesService.Current.TargetPackageName = target;
        PreferencesService.Save();
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (!_updatingDevices) _deviceStore.SelectedDevice = value;
        OnPropertyChanged(nameof(CanUseSelectedDevice));
        OnPropertyChanged(nameof(CanMirrorSelectedDevice));
        OnPropertyChanged(nameof(MirrorActionText));
        OnPropertyChanged(nameof(HasPlatformNotice));
        OnPropertyChanged(nameof(PlatformNotice));
    }

    partial void OnDiscoveryMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasDiscoveryError));
        OnPropertyChanged(nameof(DeviceCountSubtitle));
    }
    partial void OnToolCheckErrorChanged(string value) => OnPropertyChanged(nameof(ToolHealthSummary));

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var selected = _deviceStore.SelectedDevice ?? SelectedDevice;
            _updatingDevices = true;
            try
            {
                for (var index = 0; index < devices.Count; index++)
                {
                    if (index == Devices.Count) Devices.Add(devices[index]);
                    else if (!SameDeviceData(Devices[index], devices[index])) Devices[index] = devices[index];
                }
                while (Devices.Count > devices.Count) Devices.RemoveAt(Devices.Count - 1);

                SelectedDevice = Devices.FirstOrDefault(d => selected != null && d.Serial == selected.Serial && d.Platform == selected.Platform)
                    ?? Devices.FirstOrDefault(d => d.ConnectionState == DeviceConnectionState.Online)
                    ?? Devices.FirstOrDefault();
            }
            finally { _updatingDevices = false; }
            _deviceStore.SelectedDevice = SelectedDevice;
            OnlineDeviceCount = devices.Count(d => d.ConnectionState == DeviceConnectionState.Online);
            DevicesNeedingAttentionCount = devices.Count - OnlineDeviceCount;
            OnPropertyChanged(nameof(CanUseSelectedDevice));
            OnPropertyChanged(nameof(CanMirrorSelectedDevice));
        });
    }

    private static bool SameDeviceData(DeviceInfo a, DeviceInfo b) =>
        a.Serial == b.Serial && a.Platform == b.Platform && a.ConnectionState == b.ConnectionState &&
        a.IsTemporarilyUnavailable == b.IsTemporarilyUnavailable &&
        a.Name == b.Name && a.Model == b.Model && a.OsVersion == b.OsVersion &&
        a.BatteryLevel == b.BatteryLevel && a.BatteryStatus == b.BatteryStatus &&
        a.Manufacturer == b.Manufacturer && a.Product == b.Product && a.UsbInfo == b.UsbInfo &&
        a.Notes == b.Notes && a.Tag == b.Tag;

    public void OnDeviceSelected(DeviceInfo device)
    {
        SelectedDevice = Devices.FirstOrDefault(d => d.Serial == device.Serial && d.Platform == device.Platform) ?? device;
    }

    public void RefreshMirrorState() => OnPropertyChanged(nameof(MirrorActionText));

    private void OnMirrorStateChanged() =>
        _dispatcher.Post(() => { if (Volatile.Read(ref _disposed) == 0) RefreshMirrorState(); });

    private void OnDeviceConnected(DeviceInfo device) => AddActivity($"{device.DisplayName} connected");
    private void OnDeviceDisconnected(DeviceInfo device) => AddActivity($"{device.DisplayName} disconnected");
    private void OnDiscoveryStatusChanged(string? error) => _dispatcher.Post(() =>
    {
        if (Volatile.Read(ref _disposed) == 0) DiscoveryMessage = error ?? string.Empty;
    });
    private void OnCaptureStarted(LogSession session)
    {
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            ActiveSessionCount = _sessionService.ActiveSessions.Count;
            AddActivity($"Capture started: {session.DeviceName}");
        });
    }
    private void OnCaptureStopped(LogSession session)
    {
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            ActiveSessionCount = _sessionService.ActiveSessions.Count;
            AddActivity($"Capture stopped: {session.DeviceName}");
        });
    }

    private void AddActivity(string message)
    {
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            RecentActivity.Insert(0, $"{DateTime.Now:HH:mm}  {message}");
            while (RecentActivity.Count > 20) RecentActivity.RemoveAt(RecentActivity.Count - 1);
            OnPropertyChanged(nameof(HasRecentActivity));
        });
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        IsRefreshingDevices = true;
        try { await _deviceMonitor.PollDevicesAsync(); }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Dashboard] Device refresh failed");
            DiscoveryMessage = "Device refresh failed. Check the device tools and connection.";
        }
        finally { IsRefreshingDevices = false; }
        await LoadToolStatusesAsync();
        RefreshMirrorState();
    }

    [RelayCommand]
    private async Task LoadToolStatusesAsync()
    {
        await _toolStatusGate.WaitAsync();
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0) IsLoading = true;
        });
        try
        {
            var statuses = await _dependencyChecker.CheckAllAsync();
            _dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                ToolCheckError = string.Empty;
                ToolStatuses.Clear();
                foreach (var status in statuses) ToolStatuses.Add(status);
                OnPropertyChanged(nameof(ToolHealthSummary));
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Dashboard] Tool status check failed");
            _dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0) ToolCheckError = "Could not check external tools. Try Refresh.";
            });
            AddActivity("Tool status check failed");
        }
        finally
        {
            _dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0) IsLoading = false;
            });
            _toolStatusGate.Release();
        }
    }

    [RelayCommand]
    private async Task QuickStartSessionAsync()
    {
        if (!CanUseSelectedDevice)
        {
            WelcomeMessage = "Select an online device before starting a session.";
            return;
        }
        var device = SelectedDevice!;
        if (_sessionService.GetActiveSessionForDevice(device.Serial) != null)
        {
            WelcomeMessage = $"A session is already running for {device.DisplayName}. Open Sessions to view or stop it.";
            return;
        }
        LogSession? session = null;
        try
        {
            session = _sessionService.CreateSession(device);
            var started = await _sessionService.StartCaptureAsync(session);
            WelcomeMessage = started
                ? $"Session started for {device.DisplayName}. Open Sessions to view or stop it."
                : "Failed to start session. Check device authorization and tool availability.";
            if (!started) CleanupFailedSessionDirectory(session);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Dashboard] Quick session failed");
            if (session != null) CleanupFailedSessionDirectory(session);
            WelcomeMessage = $"Could not start session: {ex.Message}";
        }
    }

    private void CleanupFailedSessionDirectory(LogSession session)
    {
        try
        {
            var directory = Path.GetFullPath(session.SessionDirectory);
            var root = Path.GetFullPath(_sessionService.SessionsRootDirectory);
            if (!directory.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !PathHelper.IsSafeLocalPath(directory) ||
                !Directory.Exists(directory) || Directory.EnumerateDirectories(directory).Any())
                return;
            var files = Directory.GetFiles(directory);
            if (files.Any(file => new FileInfo(file).Length != 0)) return;
            foreach (var file in files) File.Delete(file);
            Directory.Delete(directory);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Dashboard] Could not remove unused session directory"); }
    }

    [RelayCommand]
    private async Task QuickMirrorAsync()
    {
        if (!CanMirrorSelectedDevice)
        {
            WelcomeMessage = "Select an online Android device for screen mirroring. iOS mirroring is unavailable here.";
            return;
        }

        var device = SelectedDevice!;
        try
        {
            if (_scrcpyService.IsRunning && _scrcpyService.MirroredDeviceSerial == device.Serial)
            {
                _scrcpyService.StopMirroring();
                WelcomeMessage = $"Stopped mirroring {device.DisplayName}.";
                AddActivity($"Mirror stopped: {device.DisplayName}");
            }
            else
            {
                var success = await _scrcpyService.StartMirroringAsync(device.Serial);
                WelcomeMessage = success
                    ? $"Mirroring {device.DisplayName}..."
                    : $"Could not start mirroring: {_scrcpyService.LastError ?? "Check scrcpy and device authorization."}";
                if (success) AddActivity($"Mirror started: {device.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Dashboard] Mirror failed");
            WelcomeMessage = $"Could not control mirroring: {ex.Message}";
        }
        finally { RefreshMirrorState(); }
    }

    [RelayCommand]
    private async Task QuickSnapshotAsync()
    {
        if (!CanUseSelectedDevice)
        {
            WelcomeMessage = "Select an online device to take a snapshot.";
            return;
        }

        var device = SelectedDevice!;
        try
        {
            if (!PathHelper.TryGetSafeLocalDirectory(_sessionService.SessionsRootDirectory, out var outputDir))
            {
                WelcomeMessage = "Snapshot destination must be a safe local directory.";
                return;
            }
            var deviceHash = SecurityHelper.HashSerial(device.Serial);
            var fileName = $"snapshot_{deviceHash}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png";
            var outputPath = Path.Combine(outputDir, fileName);
            var iosResult = device.Platform == DevicePlatform.iOS
                ? await _iosService.CaptureScreenshotWithStatusAsync(device.Serial, outputPath)
                : default;
            var success = device.Platform == DevicePlatform.Android
                ? await _adbService.CaptureScreenshotAsync(device.Serial, outputPath)
                : iosResult.Success;
            WelcomeMessage = success
                ? $"Snapshot saved: {fileName}"
                : device.Platform == DevicePlatform.iOS ? iosResult.Message : "Failed to capture Android snapshot. Check ADB and device authorization.";
            if (success) AddActivity($"Snapshot saved: {device.DisplayName}");
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[Dashboard] Snapshot failed");
            WelcomeMessage = $"Could not save snapshot: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task MirrorWithOptionsAsync()
    {
        if (SelectedDevice == null || SelectedDevice.Platform != DevicePlatform.Android)
        {
            WelcomeMessage = "Select an Android device for screen mirroring.";
            return;
        }

        var options = new ScrcpyOptions
        {
            BitRate = ScrcpyBitRate,
            MaxFps = int.TryParse(ScrcpyMaxFps, out var fps) ? fps : 60,
            WindowPreset = ScrcpyWindowPreset
        };

        var success = await _scrcpyService.StartMirroringAsync(SelectedDevice.Serial, options);
        WelcomeMessage = success
            ? $"Mirroring {SelectedDevice.DisplayName} ({ScrcpyBitRate}, {ScrcpyMaxFps}fps, {ScrcpyWindowPreset})..."
            : "Failed to start mirroring. Is scrcpy installed?";
    }

    [RelayCommand]
    private async Task DiscoverPortsAsync()
    {
        IsLoading = true;
        DiscoveredPorts = "Discovering...";

        var ports = await _adbService.DiscoverPairingPortsAsync();

        DiscoveredPorts = ports.Count > 0
            ? string.Join(", ", ports)
            : SecurityHelper.OfflineOnly
                ? "Wireless ADB discovery is unavailable in this offline build."
                : "Automatic discovery isn't reliable — enter IP:Port and code from the device (Settings > Developer options > Wireless debugging > Pair device).";

        IsLoading = false;
    }

    [RelayCommand]
    private async Task PairDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(PairingIpPort) || string.IsNullOrWhiteSpace(PairingCode))
        {
            WirelessStatus = "Enter IP:Port and Pairing Code.";
            return;
        }

        IsLoading = true;
        WirelessStatus = "Pairing...";

        var result = await _adbService.PairAsync(PairingIpPort, PairingCode);

        WirelessStatus = result.Success
            ? "Pairing successful! Device should connect."
            : $"Pairing failed: {result.Message}";

        if (result.Success)
        {
            await _deviceMonitor.PollDevicesAsync();
        }

        IsLoading = false;
    }

    [RelayCommand]
    private async Task ConnectWirelessDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(PairingIpPort))
        {
            WirelessStatus = "Enter IP:Port to connect.";
            return;
        }

        IsLoading = true;
        WirelessStatus = "Connecting...";

        var result = await _adbService.ConnectAsync(PairingIpPort);

        WirelessStatus = result.Success
            ? $"Connected to {PairingIpPort}"
            : $"Connection failed: {result.Message}";

        if (result.Success)
        {
            await _deviceMonitor.PollDevicesAsync();
        }

        IsLoading = false;
    }

    [RelayCommand]
    private async Task DisconnectWirelessDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(PairingIpPort))
        {
            WirelessStatus = "Enter IP:Port to disconnect.";
            return;
        }

        var result = await _adbService.DisconnectAsync(PairingIpPort);
        WirelessStatus = result.Success
            ? $"Disconnected from {PairingIpPort}"
            : $"Disconnect failed: {result.Message}";

        await _deviceMonitor.PollDevicesAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _deviceMonitor.DeviceConnected -= OnDeviceConnected;
        _deviceMonitor.DeviceDisconnected -= OnDeviceDisconnected;
        _deviceMonitor.DiscoveryStatusChanged -= OnDiscoveryStatusChanged;
        _scrcpyService.StateChanged -= OnMirrorStateChanged;
        _sessionService.CaptureStarted -= OnCaptureStarted;
        _sessionService.CaptureStopped -= OnCaptureStopped;
        GC.SuppressFinalize(this);
    }
}
