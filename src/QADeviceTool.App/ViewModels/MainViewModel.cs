using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

/// <summary>
/// Main ViewModel — manages navigation and top-level state.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly IScrcpyService _scrcpyService;
    private readonly ISessionService _sessionService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly DependencyChecker _dependencyChecker;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _selectedNavItem = "dashboard";

    [ObservableProperty]
    private int _connectedDeviceCount;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private bool _isDeviceToolsExpanded;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    public double SidebarWidth => IsSidebarCollapsed ? 48 : 220;

    // Child ViewModels
    public DashboardViewModel DashboardVM { get; }
    public SessionViewModel SessionVM { get; }
    public DeviceViewModel DeviceVM { get; }
    public AppManagementViewModel AppManagementVM { get; }
    public ShellViewModel ShellVM { get; }
    public DeepLinkViewModel DeepLinkVM { get; }
    public VitalsViewModel VitalsVM { get; }
    public FileExplorerViewModel FileExplorerVM { get; }
    public MacroViewModel MacroVM { get; }
    public StressTestViewModel StressTestVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;

        // Initialize services
        _adbService = new AdbService();
        _iosService = new IosService();
        _scrcpyService = new ScrcpyService();
        _sessionService = new SessionService(_adbService, _iosService);
        _deviceMonitor = new DeviceMonitorService(_adbService, _iosService);
        _dependencyChecker = new DependencyChecker(_adbService, _iosService, _scrcpyService);

        // Initialize child ViewModels
        DashboardVM = new DashboardViewModel(_adbService, _iosService, _scrcpyService, _sessionService, _deviceMonitor, _dependencyChecker);
        SessionVM = new SessionViewModel(_sessionService, _adbService, _iosService, _deviceMonitor);
        DeviceVM = new DeviceViewModel(_adbService, _iosService, _scrcpyService, _deviceMonitor, _sessionService);
        AppManagementVM = new AppManagementViewModel(_adbService, _iosService, _deviceMonitor, _sessionService);
        ShellVM = new ShellViewModel(_deviceMonitor, _iosService);
        DeepLinkVM = new DeepLinkViewModel(_adbService, _iosService, _deviceMonitor);
        VitalsVM = new VitalsViewModel(_adbService, _deviceMonitor);
        FileExplorerVM = new FileExplorerViewModel(_adbService, _iosService, _deviceMonitor);
        MacroVM = new MacroViewModel(new MacroService(_adbService), _adbService, _deviceMonitor);
        StressTestVM = new StressTestViewModel(_adbService, _deviceMonitor);
        SettingsVM = new SettingsViewModel(_dependencyChecker, _sessionService, _adbService);

        // Wire up device monitor events
        _deviceMonitor.DevicesChanged += devices =>
        {
            _dispatcher.BeginInvoke(() =>
            {
                ConnectedDeviceCount = devices.Count;
                StatusBarText = devices.Count > 0
                    ? $"{devices.Count} device(s) connected"
                    : "No devices connected";

                // Update global devices collection
                Devices.Clear();
                foreach (var device in devices)
                {
                    Devices.Add(device);
                }

                // Auto-select first device if none selected
                if (SelectedDevice == null && Devices.Count > 0)
                {
                    SelectedDevice = Devices[0];
                }
                // Remove selected if it's no longer connected
                var stillConnected = SelectedDevice != null && Devices.Any(d => d.Serial == SelectedDevice.Serial);
                if (!stillConnected)
                {
                    SelectedDevice = Devices.Count > 0 ? Devices[0] : null;
                }
            });
        };

        // Default view
        CurrentView = DashboardVM;

        // Start monitoring
        _deviceMonitor.StartMonitoring();
    }

    [RelayCommand]
    public void ToggleDeviceTools()
    {
        IsDeviceToolsExpanded = !IsDeviceToolsExpanded;
    }

        [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        OnPropertyChanged(nameof(SidebarWidth));
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        // Propagate selected device to all child ViewModels
        if (value != null)
        {
            DashboardVM?.OnDeviceSelected(value);
            SessionVM?.OnDeviceSelected(value);
            ShellVM?.OnDeviceSelected(value);
            DeepLinkVM?.OnDeviceSelected(value);
            VitalsVM?.OnDeviceSelected(value);
            FileExplorerVM?.OnDeviceSelected(value);
            MacroVM?.OnDeviceSelected(value);
            StressTestVM?.OnDeviceSelected(value);
            AppManagementVM?.OnDeviceSelected(value);
        }
    }

    [RelayCommand]
    public void Navigate(string destination)
    {
        var normalized = destination?.ToLowerInvariant() ?? "";
        if (CurrentView is VitalsViewModel vvm) vvm.OnNavigatedFrom();
        SelectedNavItem = normalized;
        AppLogger.Log.Debug($"[MainVM] Navigating to {normalized}");
        CurrentView = normalized switch
        {
            "dashboard" => DashboardVM,
            "sessions" => SessionVM,
            "device" or "devices" => DeviceVM,
            "apps" => AppManagementVM,
            "shell" => ShellVM,
            "deeplink" => DeepLinkVM,
            "vitals" => VitalsVM,
            "files" => FileExplorerVM,
            "macros" => MacroVM,
            "stresstest" => StressTestVM,
            "settings" => SettingsVM,
            _ => DashboardVM
        };
        if (CurrentView is VitalsViewModel vvm2) vvm2.OnNavigatedTo();
    }

    public void Cleanup()
    {
        _sessionService.StopAllCaptures();
        _scrcpyService.StopMirroring();
        _deviceMonitor.Dispose();
    }

    public void Dispose()
    {
            _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        GC.SuppressFinalize(this);
    }
}

