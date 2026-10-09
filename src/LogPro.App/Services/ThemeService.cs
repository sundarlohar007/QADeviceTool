using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace LogPro.Services;

public static class ThemeService
{
    public const string ThemeDark = "Dark";
    public const string ThemeLight = "Light";

    private const string DarkThemeSource = "Themes/DarkTheme.xaml";
    private const string LightThemeSource = "Themes/LightTheme.xaml";

    private static string _currentTheme = ThemeDark;

    public static string CurrentTheme
    {
        get => _currentTheme;
        private set
        {
            if (_currentTheme != value)
            {
                _currentTheme = value;
                ThemeChanged?.Invoke();
            }
        }
    }

    public static event Action? ThemeChanged;

    static ThemeService()
    {

    }

    public static void ApplyStartupTheme(Application app)
    {
        _currentTheme = PreferencesService.Current.ThemePreference == ThemeLight ? ThemeLight : ThemeDark;
        LoadThemeDictionary(app.Resources.MergedDictionaries, _currentTheme);
    }

    public static void SwitchTheme(string themeName)
    {
        if (themeName == _currentTheme) return;

        var merged = Application.Current.Resources.MergedDictionaries;
        LoadThemeDictionary(merged, themeName);

        PreferencesService.Update(p => p.ThemePreference = themeName);

        CurrentTheme = themeName;
    }

    private static void LoadThemeDictionary(IList<ResourceDictionary> merged, string themeName)
    {
        var toRemove = merged
            .Where(rd => rd.Source != null &&
                   (rd.Source.OriginalString.Contains("DarkTheme") ||
                    rd.Source.OriginalString.Contains("LightTheme")))
            .ToList();
        foreach (var rd in toRemove)
            merged.Remove(rd);

        var requested = themeName == ThemeLight ? LightThemeSource : DarkThemeSource;
        var fallback = themeName == ThemeLight ? DarkThemeSource : LightThemeSource;

        try
        {
            var dict = new ResourceDictionary { Source = new Uri(requested, UriKind.Relative) };
            merged.Insert(0, dict);
        }
        catch (Exception ex)
        {
            // Never let a broken theme dictionary kill startup (XamlParseException at boot).
            // Log the FULL chain (inner exceptions carry the failing resource/value) and fall back.
            var fullChain = ex.ToString();
            LogToEarlyLog(fullChain);
            try { AppLogger.Log.Error(ex, $"[ThemeService] Failed to load {requested}, falling back to {fallback}"); } catch { /* logger may not be ready */ }
            try
            {
                var fbDict = new ResourceDictionary { Source = new Uri(fallback, UriKind.Relative) };
                merged.Insert(0, fbDict);
            }
            catch (Exception fbEx)
            {
                LogToEarlyLog(fbEx.ToString());
                try { AppLogger.Log.Error(fbEx, "[ThemeService] Fallback theme also failed — starting with default WPF styles"); } catch { /* logger may not be ready */ }
            }
        }
    }

    /// <summary>Appends diagnostics to the pre-NLog startup log so failures are recoverable even if logging isn't initialized yet.</summary>
    private static void LogToEarlyLog(string message)
    {
        try
        {
            var path = Path.Combine(Helpers.PathHelper.GetAppDataDirectory(), "theme-debug.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [ThemeService]\n{Helpers.SecurityHelper.RedactSensitiveText(message, redactIdentifiers: false)}\n\n");
        }
        catch { /* cannot log the logging failure */ }
    }
}
