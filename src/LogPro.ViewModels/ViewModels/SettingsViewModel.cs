using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Helpers;
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
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly DependencyChecker _dependencyChecker;
    private readonly ISessionService _sessionService;
    private readonly IAdbService _adbService;
    private readonly IUiDispatcher _dispatcher;
    private readonly UpdateService _updateService = new();
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

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
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

    [ObservableProperty]
    private ObservableCollection<LogRetentionOption> _logRetentionOptions = new();

    [ObservableProperty]
    private LogRetentionOption? _selectedLogRetention;

    [ObservableProperty]
    private string _clearDataStatus = string.Empty;

    [ObservableProperty]
    private string _storageSummary = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _cleanupTargets = new();


    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private bool _isLightTheme;
    [ObservableProperty]
    private string _pairingIpPort = string.Empty;

    [ObservableProperty]
    private string _connectionIpPort = string.Empty;

    [ObservableProperty]
    private string _pairingCode = string.Empty;

    [ObservableProperty]
    private string _discoveredPorts = string.Empty;

    [ObservableProperty]
    private string _wirelessStatus = SecurityHelper.OfflineOnly
        ? "[!] Wireless ADB is unavailable in this offline build. Connect Android devices by USB."
        : string.Empty;

    public bool IsWirelessAvailable => !SecurityHelper.OfflineOnly;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ObservableCollection<UpdateInfo> _availableUpdates = new();

    [ObservableProperty]
    private UpdateInfo? _selectedUpdate;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private string _updateCheckDetails = string.Empty;

    [ObservableProperty]
    private bool _checkForUpdatesOnStartup;

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
        _checkForUpdatesOnStartup = PreferencesService.Current.UpdatePreferences.CheckOnStartup;
        RefreshStorageInventory();
        // Execute all heavy startup IO away from the main UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                if (_cts.Token.IsCancellationRequested) return;
                // Start dependency checks
                await CheckDependenciesAsync();
                // Auto-check for updates if enabled
                if (CheckForUpdatesOnStartup)
                {
                    var prefs = PreferencesService.Current.UpdatePreferences;
                    var hoursSinceLastCheck = (DateTime.UtcNow - prefs.LastCheckUtc).TotalHours;
                    if (hoursSinceLastCheck >= prefs.CheckIntervalHours)
                        await CheckForUpdatesAsync();
                }
            }
            catch (Exception ex)
            {
                Services.AppLogger.Log.Error(ex, "[SettingsViewModel] Initialization task failed");
            }
        }, _cts.Token);
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
        if (!LogRetentionOptions.Any(o => o.Value == currentValue))
            LogRetentionOptions.Add(new LogRetentionOption { Text = $"{currentValue} Days (custom)", Value = currentValue });
        SelectedLogRetention = LogRetentionOptions.FirstOrDefault(o => o.Value == currentValue)
            ?? LogRetentionOptions.First(o => o.Value == 7);
    }

    [RelayCommand]
    private void SaveLogRetention()
    {
        if (SelectedLogRetention != null)
        {
            var previous = PreferencesService.Current.LogRetentionDays;
            PreferencesService.Current.LogRetentionDays = SelectedLogRetention.Value;
            if (PreferencesService.Save())
                ClearDataStatus = $"Logs and completed sessions retained: {(SelectedLogRetention.Value == 0 ? "Forever" : SelectedLogRetention.Text)}";
            else
            {
                PreferencesService.Current.LogRetentionDays = previous;
                ClearDataStatus = "Could not save retention. Previous setting remains active.";
            }
        }
    }

    [RelayCommand]
    private async Task ClearAllDataAsync()
    {
        if (_sessionService.ActiveSessions.Count > 0)
        {
            ClearDataStatus = "Stop active sessions before clearing data.";
            return;
        }
        var preview = PreferencesService.PreviewClearAllData();
        RefreshStorageInventory();
        var result = await UiServices.Dialogs.ConfirmAsync(
            "Clear All Data",
            $"Review the exact folder list in Settings. This will delete preferences, {preview.Directories.Count} app-data folders and {preview.SessionDirectories.Count} completed LogPro sessions under:\n{SessionsDirectory}\n\nOther folders and active sessions will be kept. This cannot be undone. Continue?");

        if (result)
        {
            var cleared = PreferencesService.ClearAllData();
            ClearDataStatus = cleared.Failures.Count == 0
                ? $"Cleared {cleared.DeletedDirectories} folders and reset settings. Restart the application."
                : $"Partial clear: {cleared.DeletedDirectories} folders removed; {cleared.Failures.Count} failures. {cleared.Failures[0]}";
            RefreshStorageInventory();
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var logsDir = System.IO.Path.Combine(Helpers.PathHelper.GetAppDataDirectory(), "logs");
            if (System.IO.Directory.Exists(logsDir))
                OpenLocalFolder(logsDir);
            else
                ClearDataStatus = "Logs directory not found.";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Settings] OpenLogsFolder failed"); ClearDataStatus = $"Failed to open logs: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task CheckDependenciesAsync()
    {
        if (IsChecking) return;
        _dispatcher.Post(() =>
        {
            IsChecking = true;
            StatusMessage = "Checking tool availability...";
        });
        try
        {
            var statuses = await _dependencyChecker.CheckAllAsync();

            _dispatcher.Post(() =>
            {
                ToolStatuses.Clear();
                foreach (var s in statuses)
                    ToolStatuses.Add(s);
            });

            var allGood = statuses.All(s => s.IsInstalled);
            _dispatcher.Post(() =>
            {
                StatusMessage = allGood
                    ? "All tools are installed and ready!"
                    : "Some tools are missing. Check the list above.";
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Settings] Dependency check failed");
            _dispatcher.Post(() => StatusMessage = $"Dependency check failed: {ex.Message}");
        }
        finally { _dispatcher.Post(() => IsChecking = false); }
    }

    [RelayCommand]
    private void OpenSessionsFolder()
    {
        if (System.IO.Directory.Exists(SessionsDirectory))
        {
            try { OpenLocalFolder(SessionsDirectory); }
            catch (Exception ex) { ClearDataStatus = $"Could not open Sessions folder: {ex.Message}"; }
        }
    }

    [RelayCommand]
    private void BrowseSessionsFolder()
    {
        if (_sessionService.ActiveSessions.Count > 0)
        {
            StatusMessage = "Stop active sessions before changing the Sessions folder.";
            return;
        }
        var folder = UiServices.Files.OpenFolder("Select Sessions Directory");
        if (folder != null)
        {
            if (!Helpers.PathHelper.IsSafeLocalPath(folder))
            {
                StatusMessage = "[!] Sessions must be stored on a local, non-reparse-point volume.";
                return;
            }
            if (!Helpers.PathHelper.TryGetSafeLocalDirectory(folder, out var safeFolder))
            {
                StatusMessage = "[!] Sessions folder is unavailable or not writable.";
                return;
            }
            try
            {
                var probe = Path.Combine(safeFolder, $".logpro_write_probe_{Guid.NewGuid():N}");
                using (File.Create(probe)) { }
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                StatusMessage = $"[!] Sessions folder is not writable: {ex.Message}";
                return;
            }
            var previous = PreferencesService.Current.SessionsRootDirectory;
            PreferencesService.Current.SessionsRootDirectory = safeFolder;
            if (!PreferencesService.Save())
            {
                PreferencesService.Current.SessionsRootDirectory = previous;
                StatusMessage = "[!] Could not save Sessions folder.";
                return;
            }
            SessionsDirectory = safeFolder;
            _sessionService.SessionsRootDirectory = safeFolder;
            RefreshStorageInventory();
        }
    }

    [RelayCommand]
    private async Task DiscoverPortsAsync()
    {
        if (!IsWirelessAvailable) { DiscoveredPorts = WirelessStatus; return; }
        if (IsLoading) return;
        IsLoading = true;
        DiscoveredPorts = "Discovering...";
        try
        {
            var ports = await _adbService.DiscoverPairingPortsAsync();

            DiscoveredPorts = ports.Count > 0
                ? string.Join(", ", ports)
                : SecurityHelper.OfflineOnly
                    ? "Wireless ADB discovery is unavailable in this offline build."
                    : "Automatic discovery isn't reliable — enter IP:Port and code from the device (Wireless debugging > Pair device).";

        }
        catch (Exception ex) { DiscoveredPorts = $"Discovery failed: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task PairDeviceAsync()
    {
        if (!IsWirelessAvailable) { WirelessStatus = "Wireless ADB is unavailable in this offline build."; PairingCode = string.Empty; return; }
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(PairingIpPort) || string.IsNullOrWhiteSpace(PairingCode))
        {
            WirelessStatus = "Enter IP:Port and Pairing Code.";
            return;
        }

        IsLoading = true;
        WirelessStatus = "Pairing...";

        try
        {
            var result = await _adbService.PairAsync(PairingIpPort, PairingCode);

            WirelessStatus = result.Success
                ? "Pairing successful!"
                : $"Failed: {result.Message}";

        }
        catch (Exception ex) { WirelessStatus = $"Pairing failed: {ex.Message}"; }
        finally { PairingCode = string.Empty; IsLoading = false; }
    }

    [RelayCommand]
    private async Task ConnectWirelessDeviceAsync()
    {
        if (!IsWirelessAvailable) { WirelessStatus = "Wireless ADB is unavailable in this offline build."; return; }
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(ConnectionIpPort))
        {
            WirelessStatus = "Enter IP:Port to connect.";
            return;
        }

        IsLoading = true;
        WirelessStatus = "Connecting...";

        try
        {
            var result = await _adbService.ConnectAsync(ConnectionIpPort);

            WirelessStatus = result.Success
                ? $"Connected to {ConnectionIpPort}"
                : $"Failed: {result.Message}";

        }
        catch (Exception ex) { WirelessStatus = $"Connection failed: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task DisconnectWirelessDeviceAsync()
    {
        if (!IsWirelessAvailable) { WirelessStatus = "Wireless ADB is unavailable in this offline build."; return; }
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(ConnectionIpPort))
        {
            WirelessStatus = "Enter IP:Port to disconnect.";
            return;
        }

        IsLoading = true;
        try
        {
            var result = await _adbService.DisconnectAsync(ConnectionIpPort);
            WirelessStatus = result.Success
                ? $"Disconnected from {ConnectionIpPort}"
                : $"Failed: {result.Message}";
        }
        catch (Exception ex) { WirelessStatus = $"Disconnect failed: {ex.Message}"; }
        finally { IsLoading = false; }
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
    private async Task ExportMyDataAsync()
    {
        try
        {
            var destination = UiServices.Files.SaveFile("Export LogPro Data", "ZIP archive (*.zip)|*.zip", "LogPro-data.zip");
            if (destination == null) return;
            if (!PathHelper.IsSafeLocalPath(destination)) { ClearDataStatus = "Choose a safe local export destination."; return; }
            var appData = PathHelper.GetAppDataDirectory();
            var output = Path.GetFullPath(destination);
            if (IsWithin(output, appData) || IsWithin(output, SessionsDirectory))
            {
                ClearDataStatus = "Choose an export destination outside app data and Sessions.";
                return;
            }
            var includeDiagnosticData = await UiServices.Dialogs.ConfirmAsync("Include diagnostic data?",
                "Include logs and completed session files? They may contain device or app information. Choose No to export settings only.");
            ClearDataStatus = "Exporting data...";
            await Task.Run(() =>
            {
                using var outputStream = File.Create(output);
                using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create);
                var preferences = PreferencesService.Current;
                var redacted = JsonSerializer.Serialize(new
                {
                    preferences.LogRetentionDays,
                    preferences.ThemePreference,
                    preferences.SecureMode,
                    SessionsRootDirectory = "[redacted]",
                    TargetPackageName = "[redacted]"
                });
                var entry = archive.CreateEntry("settings-redacted.json");
                using (var writer = new StreamWriter(entry.Open())) writer.Write(redacted);
                if (includeDiagnosticData)
                {
                    AddSafeFiles(archive, Path.Combine(appData, "logs"), "logs");
                    foreach (var session in PreferencesService.PreviewClearAllData().SessionDirectories)
                        AddSafeFiles(archive, session, "sessions/" + Path.GetFileName(session));
                }
            });
            ClearDataStatus = $"Exported to {output}";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Settings] ExportMyData failed"); ClearDataStatus = $"Export failed: {ex.Message}"; }
    }

    private static void AddSafeFiles(ZipArchive archive, string directory, string prefix)
    {
        if (!PathHelper.IsSafeLocalPath(directory) || !Directory.Exists(directory)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            if (!PathHelper.IsSafeLocalPath(file)) continue;
            archive.CreateEntryFromFile(file, prefix + "/" + Path.GetRelativePath(directory, file).Replace('\\', '/'));
        }
    }

    private static bool IsWithin(string file, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return file.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void OpenLocalFolder(string directory)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });

    [RelayCommand]
    private void RefreshStorageInventory()
    {
        var preview = PreferencesService.PreviewClearAllData();
        CleanupTargets.Clear();
        foreach (var path in preview.Directories.Concat(preview.SessionDirectories)) CleanupTargets.Add(path);
        StorageSummary = $"{preview.SessionDirectories.Count} completed sessions and {preview.Directories.Count} app-data folders eligible for cleanup.";
    }

    // ─── Auto-Update Commands ────────────────────────────────────

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates || _disposed != 0) return;
        _dispatcher.Post(() =>
        {
            IsCheckingUpdates = true;
            UpdateStatus = "Checking for updates...";
        });

        try
        {
            var updates = await _updateService.CheckAllAsync(_cts.Token);
            var failures = updates.Where(u => u.ReleaseNotes.StartsWith("Check failed:", StringComparison.Ordinal)).ToList();
            var unknown = updates.Where(u => u.CurrentVersion == "unknown" && !failures.Contains(u)).ToList();
            var unsupported = updates.Where(u => u.IsNewerAvailable && !u.IsInstallable).ToList();
            if (failures.Count == 0)
            {
                PreferencesService.Current.UpdatePreferences.LastCheckUtc = DateTime.UtcNow;
                PreferencesService.Save();
            }

            var suppressed = PreferencesService.Current.UpdatePreferences.SuppressedVersions;
            var available = updates
                .Where(u => u.IsInstallable && !suppressed.Contains($"{u.ToolName}:{u.LatestVersion}"))
                .ToList();

            _dispatcher.Post(() =>
            {
                AvailableUpdates.Clear();
                foreach (var u in available)
                    AvailableUpdates.Add(u);
                UpdateCheckDetails = string.Join(Environment.NewLine, updates.Select(u =>
                    $"{u.ToolName}: installed {u.CurrentVersion}; latest {(string.IsNullOrWhiteSpace(u.LatestVersion) ? "unavailable" : u.LatestVersion)}" +
                    (u.ReleaseNotes.StartsWith("Check failed:", StringComparison.Ordinal) ? $" — {u.ReleaseNotes}" :
                     u.IsNewerAvailable && !u.IsInstallable ? " — no compatible verified download" : string.Empty)));
                UpdateStatus = failures.Count > 0
                    ? $"{failures.Count} update source(s) could not be checked. Retry later."
                    : unknown.Count > 0
                    ? $"{unknown.Count} installed version(s) could not be determined. See details."
                    : unsupported.Count > 0
                    ? $"{unsupported.Count} newer release(s) have no compatible verified download. See details."
                    : available.Count > 0
                    ? $"{available.Count} update(s) available"
                    : "All tools are up to date!";
                IsCheckingUpdates = false;
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Settings] CheckForUpdates failed");
            _dispatcher.Post(() =>
            {
                UpdateStatus = $"Update check failed: {ex.Message}";
                IsCheckingUpdates = false;
            });
        }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync(UpdateInfo update)
    {
        if (update == null || IsCheckingUpdates || _disposed != 0) return;
        IsCheckingUpdates = true;
        _dispatcher.Post(() => UpdateStatus = $"Updating {update.ToolName}...");
        try
        {
            var (success, message) = await _updateService.ApplyUpdateAsync(update, ct: _cts.Token);
            _dispatcher.Post(() =>
            {
                UpdateStatus = message;
                if (success)
                {
                    var item = AvailableUpdates.FirstOrDefault(u => u.ToolName == update.ToolName);
                    if (item != null) AvailableUpdates.Remove(item);
                }
            });

            // Refresh dependency status after a tool update
            if (success && !string.Equals(update.ToolName, "logpro", StringComparison.OrdinalIgnoreCase))
                await CheckDependenciesAsync();
        }
        catch (Exception ex) { _dispatcher.Post(() => UpdateStatus = $"Update failed: {ex.Message}"); }
        finally { _dispatcher.Post(() => IsCheckingUpdates = false); }
    }

    [RelayCommand]
    private void SkipUpdate(UpdateInfo update)
    {
        if (update == null) return;
        var key = $"{update.ToolName}:{update.LatestVersion}";
        var suppressed = PreferencesService.Current.UpdatePreferences.SuppressedVersions;
        if (!suppressed.Contains(key))
        {
            suppressed.Add(key);
            PreferencesService.Save();
        }
        var item = AvailableUpdates.FirstOrDefault(u => u.ToolName == update.ToolName);
        if (item != null) AvailableUpdates.Remove(item);
        UpdateStatus = $"Skipped {update.ToolName} v{update.LatestVersion}";
    }

    [RelayCommand]
    private async Task RollbackToolAsync(string toolName)
    {
        if (IsCheckingUpdates || _disposed != 0) return;
        IsCheckingUpdates = true;
        try
        {
            UpdateStatus = $"Restoring previous {toolName} installation...";
            var (_, message) = await _updateService.RollbackLastUpdateAsync(toolName);
            UpdateStatus = message;
            await CheckDependenciesAsync();
        }
        catch (Exception ex) { UpdateStatus = $"Rollback failed: {ex.Message}"; }
        finally { IsCheckingUpdates = false; }
    }

    partial void OnCheckForUpdatesOnStartupChanged(bool value)
    {
        var prefs = PreferencesService.Current.UpdatePreferences;
        var previous = prefs.CheckOnStartup;
        prefs.CheckOnStartup = value;
        if (!PreferencesService.Save())
        {
            prefs.CheckOnStartup = previous;
            UpdateStatus = "Could not save update-check preference.";
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
