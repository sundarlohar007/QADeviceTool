using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly IUiDispatcher _dispatcher;
    private readonly IDeviceStore _deviceStore;

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _selectedNavItem = "dashboard";

    [ObservableProperty]
    private int _connectedDeviceCount;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    [ObservableProperty]
    private bool _isDeviceToolsExpanded;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    public double SidebarWidth => IsSidebarCollapsed ? 48 : 220;

    public IReadOnlyList<DeviceInfo> Devices => _deviceStore.Devices;

    public DeviceInfo? SelectedDevice
    {
        get => _deviceStore.SelectedDevice;
        set => _deviceStore.SelectedDevice = value;
    }

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
    public ProfilerViewModel ProfilerVM { get; }

    public MainViewModel(IServiceProvider services)
    {
        _dispatcher = services.GetRequiredService<IUiDispatcher>();
        _deviceStore = services.GetRequiredService<IDeviceStore>();

        // Initialize services
        _adbService = services.GetRequiredService<IAdbService>();
        _iosService = services.GetRequiredService<IIosService>();
        _scrcpyService = services.GetRequiredService<IScrcpyService>();
        _sessionService = services.GetRequiredService<ISessionService>();
        _deviceMonitor = services.GetRequiredService<IDeviceMonitorService>();
        _dependencyChecker = services.GetRequiredService<DependencyChecker>();

        _deviceStore.UpdateDevices(_deviceMonitor.CurrentDevices);

        // Initialize child ViewModels — share the container's dispatcher so the whole graph is headless-testable
        DashboardVM = new DashboardViewModel(_adbService, _iosService, _scrcpyService, _sessionService, _deviceMonitor, _dependencyChecker, _dispatcher);
        SessionVM = new SessionViewModel(_sessionService, _adbService, _iosService, _deviceMonitor, _dispatcher);
        DeviceVM = new DeviceViewModel(_adbService, _iosService, _scrcpyService, _deviceMonitor, _sessionService, _dispatcher);
        AppManagementVM = new AppManagementViewModel(_adbService, _iosService, _deviceMonitor, _sessionService, _dispatcher);
        ShellVM = new ShellViewModel(_deviceMonitor, _iosService, _dispatcher);
        DeepLinkVM = new DeepLinkViewModel(_adbService, _iosService, _deviceMonitor, _dispatcher);
        VitalsVM = new VitalsViewModel(_adbService, _deviceMonitor, _dispatcher);
        FileExplorerVM = new FileExplorerViewModel(_adbService, _iosService, _deviceMonitor, _dispatcher);
        MacroVM = new MacroViewModel(new MacroService(_adbService), _adbService, _deviceMonitor, _dispatcher);
        StressTestVM = new StressTestViewModel(_adbService, _deviceMonitor, _dispatcher);
        SettingsVM = new SettingsViewModel(_dependencyChecker, _sessionService, _adbService, _dispatcher);
        ProfilerVM = new ProfilerViewModel(_adbService, _deviceStore, _dispatcher);

        // Wire up device monitor events -> single source of truth (IDeviceStore)
        _deviceMonitor.DevicesChanged += OnDevicesChanged;

        _deviceStore.Changed += OnDevicesStoreChanged;

        // Default view
        CurrentView = DashboardVM;

        // Start monitoring
        _deviceMonitor.StartMonitoring();
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            _deviceStore.UpdateDevices(devices);
            ConnectedDeviceCount = _deviceStore.Devices.Count;
            StatusBarText = ConnectedDeviceCount > 0
                ? $"{ConnectedDeviceCount} device(s) connected"
                : "No devices connected";
        });
    }

    private void OnDevicesStoreChanged()
    {
        // Propagate device selection to all child ViewModels
        var selection = _deviceStore.SelectedDevice;
        if (selection != null)
        {
            DashboardVM?.OnDeviceSelected(selection);
            SessionVM?.OnDeviceSelected(selection);
            ShellVM?.OnDeviceSelected(selection);
            DeepLinkVM?.OnDeviceSelected(selection);
            VitalsVM?.OnDeviceSelected(selection);
            FileExplorerVM?.OnDeviceSelected(selection);
            MacroVM?.OnDeviceSelected(selection);
            StressTestVM?.OnDeviceSelected(selection);
            AppManagementVM?.OnDeviceSelected(selection);
        }
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

    [RelayCommand]
    public void Navigate(string destination)
    {
        var normalized = destination?.ToLowerInvariant() ?? "";
        if (CurrentView is VitalsViewModel vvm) vvm.OnNavigatedFrom();
        SelectedNavItem = normalized;
        AppLogger.Log.Info($"[MainVM] Navigate({normalized}) — CurrentView: {CurrentView?.GetType().Name}");
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
            "performance" => ProfilerVM,
            "settings" => SettingsVM,
            _ => DashboardVM
        };
        if (CurrentView is VitalsViewModel vvm2) vvm2.OnNavigatedTo();
    }

    public void Cleanup()
    {
        Dispose();
    }

    public void Dispose()
    {
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _deviceStore.Changed -= OnDevicesStoreChanged;

        foreach (var child in new IDisposable[]
        {
            DashboardVM, SessionVM, DeviceVM, AppManagementVM, ShellVM,
            DeepLinkVM, VitalsVM, FileExplorerVM, MacroVM, StressTestVM, ProfilerVM
        })
        {
            child?.Dispose();
        }

        _sessionService.StopAllCaptures();
        _scrcpyService.StopMirroring();
        _deviceMonitor.Dispose();
        _deviceStore.Dispose();
        GC.SuppressFinalize(this);
    }
}

