using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;

namespace QADeviceTool.ViewModels;

/// <summary>
/// Settings — dependency status and app configuration.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly DependencyChecker _dependencyChecker;
    private readonly SessionService _sessionService;
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
    private string _appVersion = "2.3.0";

    public SettingsViewModel(DependencyChecker dependencyChecker, SessionService sessionService)
    {
        _dependencyChecker = dependencyChecker;
        _sessionService = sessionService;
        _dispatcher = Application.Current.Dispatcher;
        _sessionsDirectory = sessionService.SessionsRootDirectory;

            // Execute all heavy startup IO away from the main UI thread.
            Task.Run(async () =>
            {
                // Start dependency checks
                await CheckDependenciesAsync();
            });
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
}
