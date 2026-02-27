using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;

namespace QADeviceTool.ViewModels;

/// <summary>
/// Main ViewModel — manages navigation and top-level state.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly ScrcpyService _scrcpyService;
    private readonly SessionService _sessionService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly DependencyChecker _dependencyChecker;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _selectedNavItem = "Dashboard";

    [ObservableProperty]
    private int _connectedDeviceCount;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    // Child ViewModels
    public DashboardViewModel DashboardVM { get; }
    public SessionViewModel SessionVM { get; }
    public DeviceViewModel DeviceVM { get; }
    public AppManagementViewModel AppManagementVM { get; }
    public ShellViewModel ShellVM { get; }
    public DeepLinkViewModel DeepLinkVM { get; }
    public VitalsViewModel VitalsVM { get; }
    public FileExplorerViewModel FileExplorerVM { get; }
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
        ShellVM = new ShellViewModel(_deviceMonitor);
        DeepLinkVM = new DeepLinkViewModel(_adbService, _deviceMonitor);
        VitalsVM = new VitalsViewModel(_adbService, _deviceMonitor);
        FileExplorerVM = new FileExplorerViewModel(_adbService, _iosService, _deviceMonitor);
        SettingsVM = new SettingsViewModel(_dependencyChecker, _sessionService);

        // Wire up device monitor events
        _deviceMonitor.DevicesChanged += devices =>
        {
            _dispatcher.Invoke(() =>
            {
                ConnectedDeviceCount = devices.Count;
                StatusBarText = devices.Count > 0
                    ? $"{devices.Count} device(s) connected"
                    : "No devices connected";
            });
        };

        // Default view
        CurrentView = DashboardVM;

        // Start monitoring
        _deviceMonitor.StartMonitoring();
    }

    [RelayCommand]
    private void Navigate(string destination)
    {
        SelectedNavItem = destination;
        CurrentView = destination switch
        {
            "Dashboard" => DashboardVM,
            "Sessions" => SessionVM,
            "Devices" => DeviceVM,
            "Apps" => AppManagementVM,
            "Shell" => ShellVM,
            "DeepLink" => DeepLinkVM,
            "Vitals" => VitalsVM,
            "Files" => FileExplorerVM,
            "Settings" => SettingsVM,
            _ => DashboardVM
        };
    }

    public void Cleanup()
    {
        _sessionService.StopAllCaptures();
        _scrcpyService.StopMirroring();
        _deviceMonitor.Dispose();
    }
}
