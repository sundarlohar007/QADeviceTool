using System;
using System.IO;
using System.Text.Json;
using QADeviceTool.Helpers;

namespace QADeviceTool.Services;

public class AppPreferences
{
    public string SessionsRootDirectory { get; set; } = string.Empty;
    public string TargetPackageName { get; set; } = string.Empty;
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
    }

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "Failed to save preferences.");
        }
    }
}
