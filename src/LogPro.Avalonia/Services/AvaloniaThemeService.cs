using Avalonia;
using Avalonia.Styling;
using LogPro.Services;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Services;

/// <summary>
/// Avalonia theme switching — flips the app-wide ThemeVariant and persists the choice.
/// Note: shell/view hex colors are not yet token-based, so custom surfaces stay dark until
/// the design-token sweep (next pass); Fluent control chrome switches immediately.
/// </summary>
public sealed class AvaloniaThemeService : IThemeService
{
    public string CurrentTheme => PreferencesService.Current.ThemePreference ?? ThemeDark;
    public string ThemeDark => "Dark";
    public string ThemeLight => "Light";

    public void SwitchTheme(string themeName)
    {
        var normalized = themeName == ThemeLight ? ThemeLight : ThemeDark;
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = normalized == ThemeLight ? ThemeVariant.Light : ThemeVariant.Dark;

        PreferencesService.Current.ThemePreference = normalized;
        PreferencesService.Save();
    }
}
