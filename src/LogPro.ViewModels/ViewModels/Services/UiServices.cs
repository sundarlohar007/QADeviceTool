using LogPro.Services;

namespace LogPro.ViewModels;

/// <summary>Host-registered confirmation dialogs (WPF MessageBox today, Avalonia dialogs later).</summary>
public interface IDialogService
{
    bool Confirm(string title, string message);
    void Info(string title, string message);
    void Error(string title, string message);
}

/// <summary>Host-registered file dialogs (WPF Win32 dialogs today, Avalonia storage provider later).</summary>
public interface IFileDialogService
{
    string? OpenFile(string title, string filter);
    string? SaveFile(string title, string filter, string defaultFileName);
    string? OpenFolder(string title);
}

/// <summary>Host-registered theme switching (WPF ThemeService today, Avalonia resource swap later).</summary>
public interface IThemeService
{
    string CurrentTheme { get; }
    string ThemeDark { get; }
    string ThemeLight { get; }
    void SwitchTheme(string themeName);
}

/// <summary>Host-registered clipboard access.</summary>
public interface IClipboardService
{
    void SetText(string text);
}

/// <summary>
/// Service locator for the few host-provided UI services the ViewModels need.
/// The host (WPF App / Avalonia App / tests) registers implementations at startup —
/// keeps the VM layer free of any UI-framework reference (§8.1, §4.1).
/// </summary>
public static class UiServices
{
    private static IUiDispatcher? _dispatcher;
    private static IDialogService _dialogs = new NoopDialogs();
    private static IFileDialogService _files = new NoopFileDialogs();
    private static IThemeService _theme = new NoopTheme();
    private static IClipboardService _clipboard = new NoopClipboard();

    public static IUiDispatcher Dispatcher
    {
        get => _dispatcher ?? throw new InvalidOperationException(
            "UiServices.Dispatcher not initialized — the host must register an IUiDispatcher at startup.");
        set => _dispatcher = value;
    }

    public static IDialogService Dialogs
    {
        get => _dialogs;
        set => _dialogs = value ?? new NoopDialogs();
    }

    public static IFileDialogService Files
    {
        get => _files;
        set => _files = value ?? new NoopFileDialogs();
    }

    public static IThemeService Theme
    {
        get => _theme;
        set => _theme = value ?? new NoopTheme();
    }

    public static IClipboardService Clipboard
    {
        get => _clipboard;
        set => _clipboard = value ?? new NoopClipboard();
    }

    private sealed class NoopDialogs : IDialogService
    {
        public bool Confirm(string title, string message) => true;
        public void Info(string title, string message) { }
        public void Error(string title, string message) { }
    }

    private sealed class NoopFileDialogs : IFileDialogService
    {
        public string? OpenFile(string title, string filter) => null;
        public string? SaveFile(string title, string filter, string defaultFileName) => null;
        public string? OpenFolder(string title) => null;
    }

    private sealed class NoopTheme : IThemeService
    {
        public string CurrentTheme => "Dark";
        public string ThemeDark => "Dark";
        public string ThemeLight => "Light";
        public void SwitchTheme(string themeName) { }
    }

    private sealed class NoopClipboard : IClipboardService
    {
        public void SetText(string text) { }
    }
}
