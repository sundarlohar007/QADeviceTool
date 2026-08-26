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
    void Save();
    DevicePreference GetDevicePreference(string serial);
    void SaveDevicePreference(string serial, DevicePreference pref);
    void ClearAllData();
    void CleanupOldLogs();
    void CleanupOldSessions();
}

/// <summary>
/// JSON-backed preferences. Construct with an explicit directory for test/CLI isolation;
/// the default instance targets the standard app-data directory.
/// </summary>
public sealed class PreferencesStore : IPreferencesStore
{
    private readonly string _appDataDir;

    public PreferencesStore(string? appDataDir = null)
    {
        _appDataDir = appDataDir ?? PathHelper.GetAppDataDirectory();
        if (!Directory.Exists(_appDataDir)) Directory.CreateDirectory(_appDataDir);
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

        // Apply defaults if empty
        if (string.IsNullOrWhiteSpace(Current.SessionsRootDirectory))
        {
            Current.SessionsRootDirectory = PathHelper.GetDefaultSessionsDirectory();
        }

        // Ensure device preferences dictionary is initialized
        if (Current.DevicePreferences == null)
        {
            Current.DevicePreferences = new Dictionary<string, DevicePreference>();
        }

        // SEC-06: migrate raw serial keys to hashed keys (one-time).
        var rawKeys = Current.DevicePreferences.Keys
            .Where(k => SecurityHelper.IsHashedSerialKey(k) == false).ToList();
        foreach (var rawKey in rawKeys)
        {
            var pref = Current.DevicePreferences[rawKey];
            Current.DevicePreferences.Remove(rawKey);
            Current.DevicePreferences[SecurityHelper.HashSerial(rawKey)] = pref;
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, LogProJsonContext.Default.AppPreferences);
            var tmpPath = SettingsFilePath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "Failed to save preferences.");
        }
    }

    public DevicePreference GetDevicePreference(string serial)
    {
        var key = SecurityHelper.HashSerial(serial);
        if (Current.DevicePreferences.TryGetValue(key, out var pref))
            return pref;

        var newPref = new DevicePreference();
        Current.DevicePreferences[key] = newPref;
        return newPref;
    }

    public void SaveDevicePreference(string serial, DevicePreference pref)
    {
        Current.DevicePreferences[SecurityHelper.HashSerial(serial)] = pref;
        Save();
    }

    public void ClearAllData()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                File.Delete(SettingsFilePath);
            }

            var logsDir = Path.Combine(_appDataDir, "logs");
            if (Directory.Exists(logsDir))
            {
                Directory.Delete(logsDir, true);
            }

            var sessionsDir = Path.Combine(_appDataDir, "sessions");
            if (Directory.Exists(sessionsDir))
            {
                Directory.Delete(sessionsDir, true);
            }

            Current = new AppPreferences();
            Save();

            AppLogger.Log.Info("All application data cleared.");
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "Failed to clear all data.");
        }
    }

    public void CleanupOldLogs()
    {
        try
        {
            var retentionDays = Current.LogRetentionDays;
            if (retentionDays <= 0) return;

            var logsDir = Path.Combine(_appDataDir, "logs");
            if (!Directory.Exists(logsDir)) return;

            var cutoffDate = DateTime.Now.AddDays(-retentionDays);
            var logFiles = Directory.GetFiles(logsDir, "*.txt").Concat(Directory.GetFiles(logsDir, "*.log")).ToArray();

            int deletedCount = 0;
            foreach (var file in logFiles)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffDate)
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

            var cutoffDate = DateTime.Now.AddDays(-retentionDays);
            int deletedCount = 0;
            foreach (var dir in Directory.GetDirectories(sessionsDir))
            {
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.LastWriteTime < cutoffDate)
                {
                    dirInfo.Delete(recursive: true);
                    deletedCount++;
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
    public static IPreferencesStore Instance { get; set; } = new PreferencesStore();

    public static AppPreferences Current
    {
        get => Instance.Current;
        set => Instance.Current = value;
    }

    public static void Save() => Instance.Save();
    public static DevicePreference GetDevicePreference(string serial) => Instance.GetDevicePreference(serial);
    public static void SaveDevicePreference(string serial, DevicePreference pref) => Instance.SaveDevicePreference(serial, pref);
    public static void ClearAllData() => Instance.ClearAllData();
    public static void CleanupOldLogs() => Instance.CleanupOldLogs();
    public static void CleanupOldSessions() => Instance.CleanupOldSessions();
}
