using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LogPro.Avalonia.ViewModels;
using LogPro.Avalonia.Views;

namespace LogPro.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        System.AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is System.Exception ex)
                LogPro.Services.AppLogger.Log.Fatal(ex, "Avalonia UnhandledException");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogPro.Services.AppLogger.Log.Error(e.Exception, "Avalonia UnobservedTaskException");
            e.SetObserved();
        };

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!LogPro.Helpers.ToolResolver.VerifyBundledToolsAsync(requireManifest: false).GetAwaiter().GetResult())
            {
                // Auto-updates may have changed tool files; attempt manifest regeneration
                try
                {
                    var toolsDir = LogPro.Helpers.ToolResolver.ToolsDirectory;
                    var manifestPath = System.IO.Path.Combine(AppContext.BaseDirectory, LogPro.Services.ToolManifest.DefaultFileName);
                    if (System.IO.Directory.Exists(toolsDir))
                        LogPro.Services.ToolManifest.WriteAsync(toolsDir, manifestPath).GetAwaiter().GetResult();
                }
                catch { /* best effort — may lack write permission */ }
                // Continue with degraded mode (system PATH tools)
            }

            var window = new MainWindow();
            Services.AvaloniaDialogService.Owner = window;

            LogPro.ViewModels.UiServices.Dispatcher = new Services.AvaloniaUiDispatcher();
            LogPro.ViewModels.UiServices.Dialogs = new Services.AvaloniaDialogService();
            LogPro.ViewModels.UiServices.Files = new Services.AvaloniaFileDialogService();
            LogPro.ViewModels.UiServices.Clipboard = new Services.AvaloniaClipboardService();
            LogPro.ViewModels.UiServices.Theme = new Services.AvaloniaThemeService();

            // Apply the saved theme preference at startup
            ((Services.AvaloniaThemeService)LogPro.ViewModels.UiServices.Theme).ApplyCurrentTheme(global::Avalonia.Application.Current!);

            window.DataContext = CompositionRoot.CreateMainViewModel();
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                var preferences = LogPro.Services.PreferencesService.Current;
                if (!preferences.PrivacyNoticeAccepted)
                {
                    var accepted = await LogPro.ViewModels.UiServices.Dialogs.ConfirmAsync(
                        "Privacy Notice",
                        "LogPro stores preferences, logs, screenshots and sessions locally. Optional update checks contact GitHub when enabled; diagnostic data is not included in those requests. Continue?");
                    if (!accepted) { desktop.Shutdown(); return; }
                    preferences.PrivacyNoticeAccepted = true;
                    if (!LogPro.Services.PreferencesService.Save()) { desktop.Shutdown(); return; }
                }
                LogPro.Services.PreferencesService.CleanupOldLogs();
                LogPro.Services.PreferencesService.CleanupOldSessions();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
