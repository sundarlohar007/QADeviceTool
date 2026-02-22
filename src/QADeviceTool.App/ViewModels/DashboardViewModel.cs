using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;

namespace QADeviceTool.ViewModels;

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

        // Load initial data
        _ = LoadToolStatusesAsync();
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
        var started = _sessionService.StartCapture(session);
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

        var outputDir = Helpers.PathHelper.GetDefaultSessionsDirectory();
        var fileName = $"snapshot_{SelectedDevice.Serial}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var outputPath = System.IO.Path.Combine(outputDir, fileName);

        bool success = SelectedDevice.Platform == DevicePlatform.Android
            ? await _adbService.CaptureScreenshotAsync(SelectedDevice.Serial, outputPath)
            : await _iosService.CaptureScreenshotAsync(SelectedDevice.Serial, outputPath);

        WelcomeMessage = success
            ? $"Snapshot saved: {fileName}"
            : "Failed to capture snapshot.";
    }
}
