using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LogPro.Helpers;

namespace LogPro.Services;

public class AppPreferences
{
    public string SessionsRootDirectory { get; set; } = string.Empty;
    public string TargetPackageName { get; set; } = string.Empty;
    public Dictionary<string, DevicePreference> DevicePreferences { get; set; } = new();
    public int LogRetentionDays { get; set; } = 7;
    public string? ThemePreference { get; set; }
    public bool SecureMode { get; set; } = true; // §10: redaction on by default
    public bool PrivacyNoticeAccepted { get; set; } = false;
    public Models.UpdatePreferences UpdatePreferences { get; set; } = new();
}

public class DevicePreference
{
    public string Notes { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public DateTime? LastConnected { get; set; }
}

/// <summary>Instance-based preferences store (A7 de-static) — swappable for tests/CLI isolation.</summary>
public interface IPreferencesStore
{
    AppPreferences Current { get; set; }
    string SettingsFilePath { get; }
    bool Save();
    bool Update(Action<AppPreferences> change) { change(Current); return Save(); }
    DevicePreference GetDevicePreference(string serial);
    void SaveDevicePreference(string serial, DevicePreference pref);
    bool TrySaveDevicePreference(string serial, DevicePreference pref);
    DataClearResult ClearAllData();
    DataClearPreview PreviewClearAllData();
    void CleanupOldLogs();
    void CleanupOldSessions();
}

public sealed record DataClearPreview(IReadOnlyList<string> Directories, IReadOnlyList<string> SessionDirectories);
public sealed record DataClearResult(int DeletedDirectories, IReadOnlyList<string> Failures);

/// <summary>
/// JSON-backed preferences. Construct with an explicit directory for test/CLI isolation;
/// the default instance targets the standard app-data directory.
/// </summary>
public sealed class PreferencesStore : IPreferencesStore
{
    private readonly string _appDataDir;
    private readonly object _saveLock = new();
    private string? _lastSavedJson;

    public PreferencesStore(string? appDataDir = null)
    {
        _appDataDir = appDataDir ?? PathHelper.GetAppDataDirectory();
        if (!PathHelper.IsSafeLocalPath(_appDataDir))
            throw new ArgumentException("Application data must be stored on a local volume.", nameof(appDataDir));
        if (!Directory.Exists(_appDataDir)) Directory.CreateDirectory(_appDataDir);
        PathHelper.RestrictDirectoryAccess(_appDataDir);
        SettingsFilePath = Path.Combine(_appDataDir, "settings.json");
        Load();
    }

    public string SettingsFilePath { get; }

    public AppPreferences Current { get; set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                _lastSavedJson = json;
                Current = JsonSerializer.Deserialize(json, LogProJsonContext.Default.AppPreferences) ?? new AppPreferences();
            }
        }
        catch (JsonException ex)
        {
            AppLogger.Log.Warn(ex, "Failed to deserialize preferences, backing up corrupted file");
            try
            {
                var corruptedPath = SettingsFilePath + ".corrupt." + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Copy(SettingsFilePath, corruptedPath);
            }
            catch (Exception copyEx)
            {
                AppLogger.Log.Warn(copyEx, "Failed to back up corrupted preferences file");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "Failed to load preferences, using defaults");
        }

        var normalized = false;
        if (File.Exists(SettingsFilePath + ".backup"))
        {
            try
            {
                if (_lastSavedJson == null || !IsValidSettings(_lastSavedJson))
                {
                    Current = JsonSerializer.Deserialize(File.ReadAllText(SettingsFilePath + ".backup"), LogProJsonContext.Default.AppPreferences) ?? new();
                    normalized = true;
                }
            }
            catch (Exception ex) { AppLogger.Log.Warn(ex, "Preferences backup could not be restored"); }
        }
        // Apply defaults for unsafe or malformed persisted values.
        if (!PathHelper.IsSafeLocalPath(Current.SessionsRootDirectory))
        {
            Current.SessionsRootDirectory = PathHelper.GetDefaultSessionsDirectory();
            normalized = true;
        }

        // Ensure device preferences dictionary is initialized
        if (Current.DevicePreferences == null)
        {
            Current.DevicePreferences = new Dictionary<string, DevicePreference>();
            normalized = true;
        }
        if (Current.UpdatePreferences == null) { Current.UpdatePreferences = new Models.UpdatePreferences(); normalized = true; }
        if (Current.UpdatePreferences.SuppressedVersions == null) { Current.UpdatePreferences.SuppressedVersions = new List<string>(); normalized = true; }
        if (Current.UpdatePreferences.CheckIntervalHours is < 1 or > 720)
        { Current.UpdatePreferences.CheckIntervalHours = 24; normalized = true; }
        if (Current.LogRetentionDays is < 0 or > 3650)
        { Current.LogRetentionDays = 7; normalized = true; }
        if (Current.ThemePreference is not (null or "Dark" or "Light"))
        { Current.ThemePreference = "Dark"; normalized = true; }

        // SEC-06: migrate raw serial keys to hashed keys (one-time).
        var rawKeys = Current.DevicePreferences.Keys
            .Where(k => SecurityHelper.IsHashedSerialKey(k) == false).ToList();
        foreach (var rawKey in rawKeys)
        {
            var pref = Current.DevicePreferences[rawKey];
            Current.DevicePreferences.Remove(rawKey);
            Current.DevicePreferences[SecurityHelper.HashSerial(rawKey)] = pref;
        }

        if (rawKeys.Count > 0 || normalized) Save();
    }

    private static bool IsValidSettings(string json)
    {
        try { return JsonSerializer.Deserialize(json, LogProJsonContext.Default.AppPreferences) != null; }
        catch (JsonException) { return false; }
    }

    public bool Save()
    {
        return TrySave();
    }

    private bool TrySave(AppPreferences? snapshot = null)
    {
        lock (_saveLock)
        {
            try
            {
                if (!PathHelper.IsSafeLocalPath(SettingsFilePath)) return false;
                using var mutex = new Mutex(false, "LogProSettings_" + SecurityHelper.HashSerial(Path.GetFullPath(SettingsFilePath).ToUpperInvariant()));
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return false;
                    var currentDisk = File.Exists(SettingsFilePath) ? File.ReadAllText(SettingsFilePath) : null;
                    if (currentDisk != _lastSavedJson) return false; // Another process saved newer preferences; do not overwrite them.
                    var json = JsonSerializer.Serialize(snapshot ?? Current, LogProJsonContext.Default.AppPreferences);
                    var tmpPath = SettingsFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(tmpPath, json);
                        if (File.Exists(SettingsFilePath)) File.Replace(tmpPath, SettingsFilePath, SettingsFilePath + ".backup");
                        else File.Move(tmpPath, SettingsFilePath);
                        _lastSavedJson = json;
                    }
                    finally { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log.Error(ex, "Failed to save preferences.");
                return false;
            }
        }
    }

    public bool Update(Action<AppPreferences> change)
    {
        lock (_saveLock)
        {
            var snapshot = JsonSerializer.Serialize(Current, LogProJsonContext.Default.AppPreferences);
            var candidate = JsonSerializer.Deserialize(snapshot, LogProJsonContext.Default.AppPreferences)!;
            change(candidate);
            if (!TrySave(candidate)) return false;
            Current = candidate;
            return true;
        }
    }

    public DevicePreference GetDevicePreference(string serial)
    {
        lock (_saveLock)
        {
            if (!Current.DevicePreferences.TryGetValue(SecurityHelper.HashSerial(serial), out var pref)) return new();
            return new() { Notes = pref.Notes, Tag = pref.Tag, LastConnected = pref.LastConnected };
        }
    }
    public void SaveDevicePreference(string serial, DevicePreference pref) => TrySaveDevicePreference(serial, pref);
    public bool TrySaveDevicePreference(string serial, DevicePreference pref) => Update(current =>
        current.DevicePreferences[SecurityHelper.HashSerial(serial)] = new()
        { Notes = pref.Notes, Tag = pref.Tag, LastConnected = pref.LastConnected });

    public DataClearPreview PreviewClearAllData()
    {
        var directories = new[]
        {
            Path.Combine(_appDataDir, "logs"),
            Path.Combine(_appDataDir, "Macros"),
            Path.Combine(_appDataDir, "crash-reports")
        }.Where(d => Directory.Exists(d) && PathHelper.IsSafeLocalPath(d)).ToArray();
        return new DataClearPreview(directories, GetManagedSessionDirectories().Select(s => s.Path).ToArray());
    }

    public DataClearResult ClearAllData()
    {
        var failures = new List<string>();
        var deleted = 0;
        var preview = PreviewClearAllData();
        foreach (var directory in preview.Directories.Concat(preview.SessionDirectories))
        {
            try
            {
                if (Directory.Exists(directory) && PathHelper.IsSafeLocalPath(directory))
                {
                    Directory.Delete(directory, true);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{directory}: {ex.Message}");
                AppLogger.Log.Warn(ex, "[PreferencesStore] Failed to clear data directory");
            }
        }
        if (failures.Count == 0)
        {
            try
            {
                if (File.Exists(SettingsFilePath)) File.Delete(SettingsFilePath);
                foreach (var backup in Directory.GetFiles(_appDataDir, "settings.json.*"))
                    if (PathHelper.IsSafeLocalPath(backup)) File.Delete(backup);
                _lastSavedJson = null;
                Current = new AppPreferences { SessionsRootDirectory = PathHelper.GetDefaultSessionsDirectory() };
                if (!Save()) failures.Add("Settings could not be reset.");
            }
            catch (Exception ex) { failures.Add($"Settings: {ex.Message}"); }
        }
        return new DataClearResult(deleted, failures);
    }

    private IEnumerable<(string Path, DateTime? EndTime)> GetManagedSessionDirectories()
    {
        var root = Current.SessionsRootDirectory;
        if (!PathHelper.IsSafeLocalPath(root) || !Directory.Exists(root)) yield break;
        string[] directories;
        try { directories = Directory.GetDirectories(root); }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[PreferencesStore] Could not enumerate sessions"); directories = []; }
        foreach (var directory in directories)
        {
            if (!PathHelper.IsSafeLocalPath(directory)) continue;
            var metadata = Path.Combine(directory, "session.json");
            if (!File.Exists(metadata) || !PathHelper.IsSafeLocalPath(metadata)) continue;
            var end = ReadCompletedSessionEnd(metadata);
            if (end.HasValue) yield return (directory, end);
        }
    }

    private static DateTime? ReadCompletedSessionEnd(string metadata)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadata));
            var value = document.RootElement;
            if (!value.TryGetProperty("Id", out var id) ||
                !System.Text.RegularExpressions.Regex.IsMatch(id.GetString() ?? "", "^[0-9a-fA-F]{8}$") ||
                !value.TryGetProperty("Name", out var name) || string.IsNullOrWhiteSpace(name.GetString()) ||
                !value.TryGetProperty("StartTime", out var started) || !started.TryGetDateTime(out _)) return null;
            if (value.TryGetProperty("EndTime", out var ended) && ended.ValueKind == JsonValueKind.String &&
                ended.TryGetDateTime(out var parsed)) return parsed;
            return null;
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[PreferencesStore] Invalid session metadata during cleanup"); return null; }
    }

    public void CleanupOldLogs()
    {
        try
        {
            var retentionDays = Current.LogRetentionDays;
            if (retentionDays <= 0) return;

            var logsDir = Path.Combine(_appDataDir, "logs");
            if (!Directory.Exists(logsDir)) return;

            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
            var logFiles = Directory.GetFiles(logsDir, "*.txt").Concat(Directory.GetFiles(logsDir, "*.log")).ToArray();

            int deletedCount = 0;
            foreach (var file in logFiles)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTimeUtc < cutoffDate)
                {
                    fileInfo.Delete();
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                AppLogger.Log.Info($"Cleaned up {deletedCount} old log files (retention: {retentionDays} days).");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Debug(ex, "Failed to cleanup old logs.");
        }
    }

    /// <summary>Purges session directories (logs, screenshots, recordings) older than retention. SEC-02/03, COMP-01.</summary>
    public void CleanupOldSessions()
    {
        try
        {
            var retentionDays = Current.LogRetentionDays;
            if (retentionDays <= 0) return;

            var sessionsDir = Current.SessionsRootDirectory;
            if (string.IsNullOrWhiteSpace(sessionsDir) || !Directory.Exists(sessionsDir)) return;

            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
            int deletedCount = 0;
            foreach (var session in GetManagedSessionDirectories())
            {
                if (session.EndTime is DateTime ended && ended.ToUniversalTime() < cutoffDate)
                {
                    try { Directory.Delete(session.Path, true); deletedCount++; }
                    catch (Exception ex) { AppLogger.Log.Warn(ex, "Failed to cleanup session"); }
                }
            }

            if (deletedCount > 0)
            {
                AppLogger.Log.Info($"Cleaned up {deletedCount} old session directories (retention: {retentionDays} days).");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "Failed to cleanup old sessions.");
        }
    }
}

/// <summary>
/// Static facade over <see cref="PreferencesService.Instance"/> — preserves the existing
/// call sites while the instance seam enables test/CLI isolation (A7).
/// </summary>
public static class PreferencesService
{
    private static readonly Lazy<IPreferencesStore> Default = new(() => new PreferencesStore());
    private static readonly AsyncLocal<IPreferencesStore?> Override = new();
    public static IPreferencesStore Instance { get => Override.Value ?? Default.Value; set => Override.Value = value; }

    public static AppPreferences Current
    {
        get => Instance.Current;
        set => Instance.Current = value;
    }

    public static bool Save() => Instance.Save();
    public static bool Update(Action<AppPreferences> change) => Instance.Update(change);
    public static DevicePreference GetDevicePreference(string serial) => Instance.GetDevicePreference(serial);
    public static void SaveDevicePreference(string serial, DevicePreference pref) => Instance.SaveDevicePreference(serial, pref);
    public static bool TrySaveDevicePreference(string serial, DevicePreference pref) => Instance.TrySaveDevicePreference(serial, pref);
    public static DataClearResult ClearAllData() => Instance.ClearAllData();
    public static DataClearPreview PreviewClearAllData() => Instance.PreviewClearAllData();
    public static void CleanupOldLogs() => Instance.CleanupOldLogs();
    public static void CleanupOldSessions() => Instance.CleanupOldSessions();
}
