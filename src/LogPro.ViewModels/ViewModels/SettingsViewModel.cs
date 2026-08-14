using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

public class LogRetentionOption
{
    public string Text { get; set; } = string.Empty;
    public int Value { get; set; }
}

/// <summary>
/// Settings — dependency status and app configuration.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly DependencyChecker _dependencyChecker;
    private readonly ISessionService _sessionService;
    private readonly IAdbService _adbService;
    private readonly IUiDispatcher _dispatcher;

    [ObservableProperty]
    private ObservableCollection<ToolStatus> _toolStatuses = new();

    [ObservableProperty]
    private string _sessionsDirectory;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _appVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    [ObservableProperty]
    private ObservableCollection<LogRetentionOption> _logRetentionOptions = new();

    [ObservableProperty]
    private LogRetentionOption? _selectedLogRetention;

    [ObservableProperty]
    private string _clearDataStatus = string.Empty;


    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private bool _isLightTheme;
    [ObservableProperty]
    private string _pairingIpPort = string.Empty;

    [ObservableProperty]
    private string _pairingCode = string.Empty;

    [ObservableProperty]
    private string _discoveredPorts = string.Empty;

    [ObservableProperty]
    private string _wirelessStatus = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public SettingsViewModel(DependencyChecker dependencyChecker, ISessionService sessionService, IAdbService adbService, IUiDispatcher? dispatcher = null)
    {
        _dependencyChecker = dependencyChecker;
        _sessionService = sessionService;
        _adbService = adbService;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;
        _sessionsDirectory = sessionService.SessionsRootDirectory;

        InitializeLogRetentionOptions();


        IsDarkTheme = UiServices.Theme.CurrentTheme == UiServices.Theme.ThemeDark;
        IsLightTheme = !IsDarkTheme;
        // Execute all heavy startup IO away from the main UI thread.
        Task.Run(async () =>
        {
            // Start dependency checks
            await CheckDependenciesAsync();
        });
    }

    private void InitializeLogRetentionOptions()
    {
        LogRetentionOptions.Clear();
        LogRetentionOptions.Add(new LogRetentionOption { Text = "1 Day", Value = 1 });
        LogRetentionOptions.Add(new LogRetentionOption { Text = "3 Days", Value = 3 });
        LogRetentionOptions.Add(new LogRetentionOption { Text = "7 Days", Value = 7 });
        LogRetentionOptions.Add(new LogRetentionOption { Text = "30 Days", Value = 30 });
        LogRetentionOptions.Add(new LogRetentionOption { Text = "Forever", Value = 0 });

        var currentValue = PreferencesService.Current.LogRetentionDays;
        SelectedLogRetention = LogRetentionOptions.FirstOrDefault(o => o.Value == currentValue)
            ?? LogRetentionOptions.First(o => o.Value == 7);
    }

    [RelayCommand]
    private void SaveLogRetention()
    {
        if (SelectedLogRetention != null)
        {
            PreferencesService.Current.LogRetentionDays = SelectedLogRetention.Value;
            PreferencesService.Save();
            ClearDataStatus = $"Log retention saved: {(SelectedLogRetention.Value == 0 ? "Forever" : SelectedLogRetention.Text)}";
        }
    }

    [RelayCommand]
    private void ClearAllData()
    {
        var result = UiServices.Dialogs.Confirm(
            "Clear All Data",
            "This will delete all preferences, logs, and cached data. This action cannot be undone.\n\nAre you sure you want to continue?");

        if (result)
        {
            PreferencesService.ClearAllData();
            ClearDataStatus = "All data has been cleared. Please restart the application.";
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var logsDir = System.IO.Path.Combine(Helpers.PathHelper.GetAppDataDirectory(), "logs");
            if (System.IO.Directory.Exists(logsDir))
                System.Diagnostics.Process.Start("explorer.exe", logsDir);
            else
                ClearDataStatus = "Logs directory not found.";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Settings] OpenLogsFolder failed"); ClearDataStatus = $"Failed to open logs: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task CheckDependenciesAsync()
    {
        IsChecking = true;
        StatusMessage = "Checking tool availability...";

        var statuses = await _dependencyChecker.CheckAllAsync();

        _dispatcher.Post(() =>
        {
            ToolStatuses.Clear();
            foreach (var s in statuses)
                ToolStatuses.Add(s);
        });

        var allGood = statuses.All(s => s.IsInstalled);
        StatusMessage = allGood
            ? "All tools are installed and ready!"
            : "Some tools are missing. Check the list above.";
        IsChecking = false;
    }

    [RelayCommand]
    private void OpenSessionsFolder()
    {
        if (System.IO.Directory.Exists(SessionsDirectory))
        {
            System.Diagnostics.Process.Start("explorer.exe", SessionsDirectory);
        }
    }

    [RelayCommand]
    private void BrowseSessionsFolder()
    {
        var folder = UiServices.Files.OpenFolder("Select Sessions Directory");
        if (folder != null)
        {
            SessionsDirectory = folder;
            _sessionService.SessionsRootDirectory = folder;
            PreferencesService.Current.SessionsRootDirectory = folder;
            PreferencesService.Save();
        }
    }

    [RelayCommand]
    private async Task DiscoverPortsAsync()
    {
        IsLoading = true;
        DiscoveredPorts = "Discovering...";

        var ports = await _adbService.DiscoverPairingPortsAsync();

        DiscoveredPorts = ports.Count > 0
            ? string.Join(", ", ports)
            : "Automatic discovery isn't reliable — enter IP:Port and code from the device (Wireless debugging > Pair device).";

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
            ? "Pairing successful!"
            : $"Failed: {result.Message}";

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
            : $"Failed: {result.Message}";

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
            : $"Failed: {result.Message}";
    }
    [RelayCommand]
    private void SwitchToDarkTheme()
    {
        UiServices.Theme.SwitchTheme(UiServices.Theme.ThemeDark);
        IsDarkTheme = true;
        IsLightTheme = false;
    }

    [RelayCommand]
    private void SwitchToLightTheme()
    {
        UiServices.Theme.SwitchTheme(UiServices.Theme.ThemeLight);
        IsDarkTheme = false;
        IsLightTheme = true;
    }

    [RelayCommand]
    private void ExportMyData()
    {
        try
        {
            var appDataDir = Helpers.PathHelper.GetAppDataDirectory();
            if (System.IO.Directory.Exists(appDataDir))
            {
                System.Diagnostics.Process.Start("explorer.exe", appDataDir);
                ClearDataStatus = "App data folder opened in Explorer.";
            }
            else
                ClearDataStatus = "App data folder not found.";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Settings] ExportMyData failed"); ClearDataStatus = $"Export failed: {ex.Message}"; }
    }

}