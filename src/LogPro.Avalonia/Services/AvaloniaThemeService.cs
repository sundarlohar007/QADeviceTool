using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using LogPro.Services;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Services;

/// <summary>
/// Avalonia theme switching — deterministic palette swap: every brush key used by the
/// views ({DynamicResource BrushX}) is (re)set on Application.Resources for the active
/// variant, and Fluent control chrome follows RequestedThemeVariant. The choice persists.
/// </summary>
public sealed class AvaloniaThemeService : IThemeService
{
    private static readonly Dictionary<string, Color> Dark = new()
    {
        ["BrushVoid"] = Color.Parse("#0E0E12"),
        ["BrushBase"] = Color.Parse("#141418"),
        ["BrushSurface"] = Color.Parse("#17171C"),
        ["BrushElevated"] = Color.Parse("#1E1E24"),
        ["BrushBorder"] = Color.Parse("#232329"),
        ["BrushTextPrimary"] = Color.Parse("#E8EAED"),
        ["BrushTextSecondary"] = Color.Parse("#9AA0A6"),
        ["BrushTextMuted"] = Color.Parse("#5A6066"),
        ["BrushLogText"] = Color.Parse("#C8CCD0"),
        ["BrushAccent"] = Color.Parse("#4FD1C5"),
        ["BrushOk"] = Color.Parse("#4ADE80"),
        ["BrushWarn"] = Color.Parse("#F59E0B"),
        ["BrushDanger"] = Color.Parse("#EF4444"),
        ["BrushMem"] = Color.Parse("#E879F9"),
    };

    private static readonly Dictionary<string, Color> Light = new()
    {
        ["BrushVoid"] = Color.Parse("#F5F6F8"),
        ["BrushBase"] = Color.Parse("#ECEEF2"),
        ["BrushSurface"] = Color.Parse("#FFFFFF"),
        ["BrushElevated"] = Color.Parse("#F1F3F6"),
        ["BrushBorder"] = Color.Parse("#D9DCE2"),
        ["BrushTextPrimary"] = Color.Parse("#1B1D22"),
        ["BrushTextSecondary"] = Color.Parse("#5A6066"),
        ["BrushTextMuted"] = Color.Parse("#8A909A"),
        ["BrushLogText"] = Color.Parse("#2E333A"),
        ["BrushAccent"] = Color.Parse("#0E9488"),
        ["BrushOk"] = Color.Parse("#15803D"),
        ["BrushWarn"] = Color.Parse("#B45309"),
        ["BrushDanger"] = Color.Parse("#B91C1C"),
        ["BrushMem"] = Color.Parse("#A21CAF"),
    };

    public string CurrentTheme => PreferencesService.Current.ThemePreference ?? ThemeDark;
    public string ThemeDark => "Dark";
    public string ThemeLight => "Light";

    /// <summary>Applies the saved preference at startup.</summary>
    public void ApplyCurrentTheme(Application app)
    {
        var pref = PreferencesService.Current.ThemePreference;
        AppLogger.Log.Info($"[AvaloniaTheme] Preference='{pref}'");
        Apply(app, pref ?? ThemeDark);
    }

    public void SwitchTheme(string themeName)
    {
        var normalized = themeName == ThemeLight ? ThemeLight : ThemeDark;
        if (Application.Current != null)
            Apply(Application.Current, normalized);

        PreferencesService.Current.ThemePreference = normalized;
        PreferencesService.Save();
    }

    private static void Apply(Application app, string themeName)
    {
        // Mutate the shared brush instances (defined once in LogProBrushes.axaml) —
        // DynamicResource resolution stays intact and every surface updates live.
        var palette = themeName == "Light" ? Light : Dark;
        foreach (var (key, color) in palette)
        {
            if (app.Resources.TryGetResource(key, null, out var resource) && resource is SolidColorBrush brush)
                brush.Color = color;
        }
        AppLogger.Log.Info($"[AvaloniaTheme] Applied '{themeName}'");
    }
}
