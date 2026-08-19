using System.IO;
using System.Windows;
using System.Windows.Threading;
using LogPro.Helpers;
using LogPro.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LogPro;

public partial class App : Application
{
    private static readonly string EarlyLogPath = Path.Combine(Path.GetTempPath(), "LogPro_startup-debug.log");
    private const long EarlyLogMaxBytes = 1024 * 1024; // 1 MiB cap; truncate-on-roll instead of unbounded growth.

    private void EarlyLog(string message, Exception? ex = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(EarlyLogPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                var fi = new FileInfo(EarlyLogPath);
                if (fi.Exists && fi.Length > EarlyLogMaxBytes)
                {
                    var rolled = EarlyLogPath + ".old";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(EarlyLogPath, rolled);
                }
            }
            catch { /* rotation best-effort */ }

            var logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
            if (ex != null)
            {
                logLine += $"EXCEPTION: {ex.GetType().Name}\nMESSAGE: {ex.Message}\nSTACK TRACE:\n{ex.StackTrace}\n\n";
            }
            File.AppendAllText(EarlyLogPath, logLine);
        }
        catch { /* Cannot log the logging failure */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        EarlyLog("========================================");
        EarlyLog("APP STARTUP ENTERED");

        // Register global exception handlers BEFORE any window/ViewModel creation
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // Register host UI services for the shared ViewModel layer (§8.1)
        ViewModels.UiServices.Dispatcher = new Services.WpfUiDispatcher(Dispatcher);
        ViewModels.UiServices.Dialogs = new Services.WpfDialogService();
        ViewModels.UiServices.Files = new Services.WpfFileDialogService();
        ViewModels.UiServices.Theme = new Services.WpfThemeServiceAdapter();
        ViewModels.UiServices.Clipboard = new Services.WpfClipboardService();

        EarlyLog($"Executable Path: {Environment.ProcessPath}");
        EarlyLog($"Base Directory: {AppContext.BaseDirectory}");
        EarlyLog($"Current Directory: {Environment.CurrentDirectory}");
        EarlyLog($"OS Architecture: {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
        EarlyLog($"Process Architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(";");
        EarlyLog($"PATH entries: {pathEntries.Length}");

        // Ensure native DLL paths are initialized for iOS tools
        ToolResolver.InitializeNativePaths();

        // One-time branding migration: %LOCALAPPDATA%\QAQCDeviceTool -> LogPro.
        // Must precede PreferencesService static init (below) so settings load from the new path.
        if (!Helpers.PathHelper.MigrateLegacyAppData())
        {
            EarlyLog("Legacy app-data migration deferred (target existed or move failed).");
        }


        // Apply theme BEFORE MainWindow is instantiated via StartupUri
        Services.ThemeService.ApplyStartupTheme(this);

        base.OnStartup(e);

        // Explicit window bootstrap: on .NET 10 the StartupUri window is not created
        // synchronously inside base.OnStartup — Application.Current.MainWindow is still
        // null here, which previously left the app running with no DataContext (blank UI).
        var mainWindow = new MainWindow();
        Application.Current.MainWindow = mainWindow;
        try
        {
            var vm = CreateMainViewModel();
            mainWindow.DataContext = vm;
            EarlyLog($"MainViewModel created and set as DataContext. CurrentView: {vm.CurrentView?.GetType().Name ?? "null"}");
        }
        catch (Exception vmEx)
        {
            EarlyLog("FATAL: MainViewModel creation failed", vmEx);
            try { Services.AppLogger.Log.Fatal(vmEx, "MainViewModel creation failed"); } catch { /* logger may not be ready */ }
        }
        mainWindow.Show();

        EarlyLog("Base OnStartup completed, initializing services...");

        try
        {
            // Initialize user preferences
            var prefs = Services.PreferencesService.Current;
            EarlyLog("PreferencesService initialized.");

            // Cleanup old logs based on retention settings
            Services.PreferencesService.CleanupOldLogs();
            Services.PreferencesService.CleanupOldSessions();
            // First-run privacy notice             if (!PreferencesService.Current.PrivacyNoticeAccepted)             {                 var accepted = System.Windows.MessageBox.Show(                     "LogPro stores logs, screenshots, and session data locally on this machine. No data is sent externally. This data is used for QA testing purposes only. Continue?",                     "Privacy Notice",                     System.Windows.MessageBoxButton.YesNo,                     System.Windows.MessageBoxImage.Information);                 if (accepted == System.Windows.MessageBoxResult.Yes)                 {                     PreferencesService.Current.PrivacyNoticeAccepted = true;                     PreferencesService.Save();                 }             }
            EarlyLog("Old logs cleaned up.");

            Services.AppLogger.Log.Info("========================================");
            Services.AppLogger.Log.Info("LogPro - Application Starting");
            Services.AppLogger.Log.Info("========================================");

            // Ensure sessions directory exists
            Helpers.PathHelper.EnsureSessionsDirectory();
            EarlyLog("Session directory ensured.");
        }
        catch (Exception ex)
        {
            EarlyLog("FATAL ERROR DURING INIT", ex);
        }
    }

    /// <summary>
    /// Composition root — central place to construct the object graph.
    /// Swapping an implementation (e.g. IAdbService fake for tests, IUiDispatcher
    /// for Avalonia later) happens only here.
    /// </summary>
    private static LogPro.ViewModels.MainViewModel CreateMainViewModel()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<IUiDispatcher>(_ => new Services.WpfUiDispatcher(Application.Current.Dispatcher))
            .AddSingleton<IDeviceStore, DeviceStore>()
            .AddSingleton<IAdbService, AdbService>()
            .AddSingleton<IIosService, IosService>()
            .AddSingleton<IScrcpyService, ScrcpyService>()
            .AddSingleton<ISessionService>(sp => new SessionService(
                sp.GetRequiredService<IAdbService>(),
                sp.GetRequiredService<IIosService>()))
            .AddSingleton<IDeviceMonitorService>(sp => new DeviceMonitorService(
                sp.GetRequiredService<IAdbService>(),
                sp.GetRequiredService<IIosService>()))
            .AddSingleton(sp => new DependencyChecker(
                sp.GetRequiredService<IAdbService>(),
                sp.GetRequiredService<IIosService>(),
                sp.GetRequiredService<IScrcpyService>()))
            .AddSingleton<LogPro.ViewModels.MainViewModel>()
            .BuildServiceProvider();

        return services.GetRequiredService<LogPro.ViewModels.MainViewModel>();
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        EarlyLog("DispatcherUnhandledException caught!", e.Exception);
        Services.AppLogger.Log.Fatal(e.Exception, "DispatcherUnhandledException");
        WriteCrashReport(e.Exception);

        var technicalDetails = e.Exception.ToString(); // full chain incl. inner exceptions (e.g. XamlParseException inner)
        var result = MessageBox.Show(
            "An unexpected error occurred. The application will try to continue.\n\nWould you like to copy technical details to clipboard?",
            "LogPro - Error",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try { Clipboard.SetText(technicalDetails); } catch { }
        }

        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            EarlyLog("AppDomainUnhandled Exception caught!", ex);
            Services.AppLogger.Log.Fatal(ex, "AppDomainUnhandled");
            WriteCrashReport(ex);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        EarlyLog("TaskUnobserved Exception caught!", e.Exception);
        Services.AppLogger.Log.Error(e.Exception, "TaskUnobserved");
        e.SetObserved(); // Prevent crash
    }

    protected override void OnExit(ExitEventArgs e)
    {
        EarlyLog("APPLICATION EXITING.");
        Services.AppLogger.Log.Info("Application Exiting.");
        Services.ProcessManager.Instance.KillAllTrackedProcesses();
        NLog.LogManager.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// Generates a structured crash report file for post-mortem debugging (ERR-03).
    /// </summary>
    private void WriteCrashReport(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Helpers.PathHelper.GetAppDataDirectory(), "crash-reports");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"crash-report-{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("===== LogPro Crash Report =====");
            sb.AppendLine($"Timestamp: {DateTime.Now:O}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"CLR: {Environment.Version}");
            sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
            sb.AppendLine();
            sb.AppendLine("--- Exception ---");
            var current = ex;
            int depth = 0;
            while (current != null && depth < 5)
            {
                sb.AppendLine($"[{depth}] {current.GetType().FullName}: {current.Message}");
                sb.AppendLine(current.StackTrace);
                sb.AppendLine();
                current = current.InnerException;
                depth++;
            }
            sb.AppendLine("--- Loaded Assemblies ---");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName))
                sb.AppendLine($"  {asm.FullName}");
            File.WriteAllText(filePath, sb.ToString());
            EarlyLog($"Crash report written: {filePath}");
        }
        catch (Exception writeEx)
        {
            EarlyLog("Failed to write crash report", writeEx);
        }
    }
}
