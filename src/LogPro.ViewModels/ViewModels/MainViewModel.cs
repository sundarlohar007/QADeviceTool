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
    private int _disposed;

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
        DashboardVM = new DashboardViewModel(_adbService, _iosService, _scrcpyService, _sessionService, _deviceMonitor, _dependencyChecker, _deviceStore, _dispatcher);
        SessionVM = new SessionViewModel(_sessionService, _adbService, _iosService, _deviceMonitor, _dispatcher);
        DeviceVM = new DeviceViewModel(_adbService, _iosService, _scrcpyService, _deviceMonitor, _sessionService, _dispatcher);
        DeviceVM.PropertyChanged += OnDeviceViewModelPropertyChanged;
        AppManagementVM = new AppManagementViewModel(_adbService, _iosService, _deviceMonitor, _sessionService, _dispatcher, _deviceStore);
        ShellVM = new ShellViewModel(_deviceMonitor, _iosService, _dispatcher);
        DeepLinkVM = new DeepLinkViewModel(_adbService, _iosService, _deviceMonitor, _dispatcher);
        VitalsVM = new VitalsViewModel(_adbService, _deviceMonitor, _dispatcher);
        FileExplorerVM = new FileExplorerViewModel(_adbService, _iosService, _deviceMonitor, _dispatcher);
        FileExplorerVM.PropertyChanged += OnFileExplorerPropertyChanged;
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
                ? $"{ConnectedDeviceCount} device(s) detected"
                : "No devices detected";
        });
    }

    private void OnDevicesStoreChanged()
    {
        OnPropertyChanged(nameof(SelectedDevice));
        // Propagate device selection to all child ViewModels
        var selection = _deviceStore.SelectedDevice;
        if (selection != null)
        {
            DashboardVM?.OnDeviceSelected(selection);
            if (DeviceVM != null && (DeviceVM.SelectedDevice?.Serial != selection.Serial ||
                DeviceVM.SelectedDevice.Platform != selection.Platform))
                DeviceVM.SelectedDevice = DeviceVM.Devices.FirstOrDefault(d => d.Serial == selection.Serial &&
                    d.Platform == selection.Platform) ?? selection;
            SessionVM?.OnDeviceSelected(selection);
            ShellVM?.OnDeviceSelected(selection);
            DeepLinkVM?.OnDeviceSelected(selection);
            VitalsVM?.OnDeviceSelected(selection);
            FileExplorerVM?.OnDeviceSelected(selection);
            MacroVM?.OnDeviceSelected(selection);
            StressTestVM?.OnDeviceSelected(selection);
            AppManagementVM?.OnDeviceSelected(selection);
        }
        else if (DeviceVM != null)
        {
            DeviceVM.SelectedDevice = null;
        }
    }

    private void OnDeviceViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceViewModel.SelectedDevice) && DeviceVM.SelectedDevice != null)
            _deviceStore.SelectedDevice = DeviceVM.SelectedDevice;
    }

    private void OnFileExplorerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileExplorerViewModel.SelectedDevice) && FileExplorerVM.SelectedDevice != null)
            _deviceStore.SelectedDevice = FileExplorerVM.SelectedDevice;
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
        if (CurrentView is DashboardViewModel dashboard) dashboard.RefreshMirrorState();
    }

    public void Cleanup()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _deviceStore.Changed -= OnDevicesStoreChanged;
        DeviceVM.PropertyChanged -= OnDeviceViewModelPropertyChanged;
        FileExplorerVM.PropertyChanged -= OnFileExplorerPropertyChanged;

        _sessionService.StopAllCaptures();

        foreach (var child in new IDisposable[]
        {
            DashboardVM, SessionVM, DeviceVM, AppManagementVM, ShellVM,
            DeepLinkVM, VitalsVM, FileExplorerVM, MacroVM, StressTestVM, ProfilerVM
        })
        {
            child?.Dispose();
        }

        _scrcpyService.StopMirroring();
        _deviceMonitor.Dispose();
        _deviceStore.Dispose();
        GC.SuppressFinalize(this);
    }
}

