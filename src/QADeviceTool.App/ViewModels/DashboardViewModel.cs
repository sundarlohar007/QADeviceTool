using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

/// <summary>
/// Dashboard — overview of devices, tool statuses, and quick actions.
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly ScrcpyService _scrcpyService;
    private readonly SessionService _sessionService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly DependencyChecker _dependencyChecker;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private ObservableCollection<ToolStatus> _toolStatuses = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _welcomeMessage = "Welcome to QA Device Tool";

    [ObservableProperty]
    private int _activeSessionCount;

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
    private string _wirelessStatus = string.Empty;

    public DashboardViewModel(
        AdbService adbService,
        IosService iosService,
        ScrcpyService scrcpyService,
        SessionService sessionService,
        DeviceMonitorService deviceMonitor,
        DependencyChecker dependencyChecker)
    {
        _adbService = adbService;
        _iosService = iosService;
        _scrcpyService = scrcpyService;
        _sessionService = sessionService;
        _deviceMonitor = deviceMonitor;
        _dependencyChecker = dependencyChecker;
        _dispatcher = Application.Current.Dispatcher;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;

        // Load initial data exclusively on a background thread so we don't block the UI rendering during startup
        Task.Run(async () =>
        {
            try
            {
                var keyword = PreferencesService.Current.TargetPackageName;
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    _dispatcher.Invoke(() => TargetPackageName = keyword);
                }
            }
            catch { }

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
        try
        {
            PreferencesService.Current.TargetPackageName = value?.Trim() ?? string.Empty;
            PreferencesService.Save();
        }
        catch { }
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Devices.Clear();
            foreach (var device in devices)
                Devices.Add(device);

            // Clear stale selection if device was unplugged
            if (SelectedDevice != null && !devices.Any(d => d.Serial == SelectedDevice.Serial))
                SelectedDevice = null;
            if (SelectedDevice == null && devices.Count > 0)
                SelectedDevice = devices[0];
        });
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        SelectedDevice = device;
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        IsLoading = true;
        await _deviceMonitor.PollDevicesAsync();
        IsLoading = false;
    }

    [RelayCommand]
    private async Task LoadToolStatusesAsync()
    {
        IsLoading = true;
        var statuses = await _dependencyChecker.CheckAllAsync();
        _dispatcher.Invoke(() =>
        {
            ToolStatuses.Clear();
            foreach (var status in statuses)
                ToolStatuses.Add(status);
        });
        IsLoading = false;
    }

    [RelayCommand]
    private async Task QuickStartSessionAsync()
    {
        if (SelectedDevice == null)
        {
            // Try first available device
            var devices = _deviceMonitor.CurrentDevices;
            if (devices.Count == 0)
            {
                WelcomeMessage = "No devices connected. Please connect a device first.";
                return;
            }
            SelectedDevice = devices[0];
        }

        var session = _sessionService.CreateSession(SelectedDevice);
        var started = await _sessionService.StartCaptureAsync(session);
        if (started)
        {
            ActiveSessionCount++;
            WelcomeMessage = $"Session started for {SelectedDevice.DisplayName}";
        }
        else
        {
            WelcomeMessage = "Failed to start session. Check tool availability.";
        }
    }

    [RelayCommand]
    private async Task QuickMirrorAsync()
    {
        if (SelectedDevice == null || SelectedDevice.Platform != DevicePlatform.Android)
        {
            WelcomeMessage = "Select an Android device for screen mirroring.";
            return;
        }

        var success = await _scrcpyService.StartMirroringAsync(SelectedDevice.Serial);
        WelcomeMessage = success
            ? $"Mirroring {SelectedDevice.DisplayName}..."
            : "Failed to start mirroring. Is scrcpy installed?";
    }

    [RelayCommand]
    private async Task QuickSnapshotAsync()
    {
        if (SelectedDevice == null)
        {
            WelcomeMessage = "Select a device to take a snapshot.";
            return;
        }

        var outputDir = PreferencesService.Current.SessionsRootDirectory;
        if (!System.IO.Directory.Exists(outputDir)) System.IO.Directory.CreateDirectory(outputDir);
        var deviceHash = LogPro.Helpers.SecurityHelper.HashSerial(SelectedDevice.Serial);
        var fileName = $"snapshot_{deviceHash}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var outputPath = System.IO.Path.Combine(outputDir, fileName);

        bool success = SelectedDevice.Platform == DevicePlatform.Android
            ? await _adbService.CaptureScreenshotAsync(SelectedDevice.Serial, outputPath)
            : await _iosService.CaptureScreenshotAsync(SelectedDevice.Serial, outputPath);

        WelcomeMessage = success
            ? $"Snapshot saved: {fileName}"
            : "Failed to capture snapshot.";
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
            : "No listening ports found. Ensure ADB is running.";
        
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
}
