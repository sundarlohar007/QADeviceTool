using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
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
    private readonly SessionService _sessionService;
    private readonly AdbService _adbService;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private ObservableCollection<ToolStatus> _toolStatuses = new();

    [ObservableProperty]
    private string _sessionsDirectory;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _appVersion = "3.0.0";

    [ObservableProperty]
    private ObservableCollection<LogRetentionOption> _logRetentionOptions = new();

    [ObservableProperty]
    private LogRetentionOption? _selectedLogRetention;

    [ObservableProperty]
    private string _clearDataStatus = string.Empty;

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

    public SettingsViewModel(DependencyChecker dependencyChecker, SessionService sessionService)
    {
        _dependencyChecker = dependencyChecker;
        _sessionService = sessionService;
        _adbService = new AdbService();
        _dispatcher = Application.Current.Dispatcher;
        _sessionsDirectory = sessionService.SessionsRootDirectory;

        InitializeLogRetentionOptions();

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
        var result = MessageBox.Show(
            "This will delete all preferences, logs, and cached data. This action cannot be undone.\n\nAre you sure you want to continue?",
            "Clear All Data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            PreferencesService.ClearAllData();
            ClearDataStatus = "All data has been cleared. Please restart the application.";
        }
    }

    [RelayCommand]
    private async Task CheckDependenciesAsync()
    {
        IsChecking = true;
        StatusMessage = "Checking tool availability...";

        var statuses = await _dependencyChecker.CheckAllAsync();

        _dispatcher.Invoke(() =>
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
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Sessions Directory"
        };

        if (dialog.ShowDialog() == true)
        {
            SessionsDirectory = dialog.FolderName;
            _sessionService.SessionsRootDirectory = dialog.FolderName;
            PreferencesService.Current.SessionsRootDirectory = dialog.FolderName;
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
            : "No listening ports found.";
        
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
}
