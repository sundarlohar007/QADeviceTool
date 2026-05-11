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
}

public class DevicePreference
{
    public string Notes { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public DateTime? LastConnected { get; set; }
}

public static class PreferencesService
{
    private static readonly string _settingsFilePath;
    public static AppPreferences Current { get; private set; } = new();

    static PreferencesService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QAQCDeviceTool");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        
        _settingsFilePath = Path.Combine(dir, "settings.json");
        Load();
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                Current = JsonSerializer.Deserialize<AppPreferences>(json) ?? new AppPreferences();
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
    }

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            var tmpPath = _settingsFilePath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _settingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "Failed to save preferences.");
        }
    }
    
    public static DevicePreference GetDevicePreference(string serial)
    {
        if (Current.DevicePreferences.TryGetValue(serial, out var pref))
            return pref;
        
        var newPref = new DevicePreference();
        Current.DevicePreferences[serial] = newPref;
        return newPref;
    }
    
    public static void SaveDevicePreference(string serial, DevicePreference pref)
    {
        Current.DevicePreferences[serial] = pref;
        Save();
    }

    public static void ClearAllData()
    {
        try
        {
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QAQCDeviceTool");
            
            if (File.Exists(_settingsFilePath))
            {
                File.Delete(_settingsFilePath);
            }

            var logsDir = Path.Combine(appDataDir, "logs");
            if (Directory.Exists(logsDir))
            {
                Directory.Delete(logsDir, true);
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

    public static void CleanupOldLogs()
    {
        try
        {
            var retentionDays = Current.LogRetentionDays;
            if (retentionDays <= 0) return;

            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QAQCDeviceTool",
                "logs");

            if (!Directory.Exists(logsDir)) return;

            var cutoffDate = DateTime.Now.AddDays(-retentionDays);
            var logFiles = Directory.GetFiles(logsDir, "*.log");

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
}
