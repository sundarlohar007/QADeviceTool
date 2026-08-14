using System.Windows;
using LogPro.ViewModels;
using Microsoft.Win32;

namespace LogPro.Services;

/// <summary>WPF implementations of the host UI services consumed by the shared ViewModels.</summary>
public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void Info(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}

public sealed class WpfFileDialogService : IFileDialogService
{
    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? OpenFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

public sealed class WpfThemeServiceAdapter : IThemeService
{
    public string CurrentTheme => ThemeService.CurrentTheme;
    public string ThemeDark => ThemeService.ThemeDark;
    public string ThemeLight => ThemeService.ThemeLight;
    public void SwitchTheme(string themeName) => ThemeService.SwitchTheme(themeName);
}

public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        try { Clipboard.SetText(text); } catch { /* clipboard busy — best effort */ }
    }
}
