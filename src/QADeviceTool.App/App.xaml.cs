using System.IO;
using System.Windows;
using System.Windows.Threading;
using LogPro.Helpers;

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

        EarlyLog($"Executable Path: {Environment.ProcessPath}");
        EarlyLog($"Base Directory: {AppContext.BaseDirectory}");
        EarlyLog($"Current Directory: {Environment.CurrentDirectory}");
        EarlyLog($"OS Architecture: {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
        EarlyLog($"Process Architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        EarlyLog($"PATH Variable: {Environment.GetEnvironmentVariable("PATH")}");

        // Ensure native DLL paths are initialized for iOS tools
        ToolResolver.InitializeNativePaths();


        // Apply theme BEFORE MainWindow is instantiated via StartupUri
        Services.ThemeService.ApplyStartupTheme(this);

        base.OnStartup(e);

        // Set up MainViewModel after window creation (moved from XAML to avoid
        // duplicate VM creation during theme switches via ThemeService)
        if (this.MainWindow is MainWindow mw)
        {
            mw.DataContext = new LogPro.ViewModels.MainViewModel();
        }EarlyLog("Base OnStartup completed, initializing services...");

        try
        {
            // Initialize user preferences
            var prefs = Services.PreferencesService.Current;
            EarlyLog("PreferencesService initialized.");

            // Cleanup old logs based on retention settings
            Services.PreferencesService.CleanupOldLogs();
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

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        EarlyLog("DispatcherUnhandledException caught!", e.Exception);
        Services.AppLogger.Log.Fatal(e.Exception, "DispatcherUnhandledException");
        
        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}\n\nCheck startup log at:\n{EarlyLogPath}",
            "LogPro - Error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
            
        e.Handled = true; // Prevent crash � keep app running
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            EarlyLog("AppDomainUnhandled Exception caught!", ex);
            Services.AppLogger.Log.Fatal(ex, "AppDomainUnhandled");
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
        Services.ProcessManagerService.KillAllTrackedProcesses();
        NLog.LogManager.Shutdown(); // Flush and close logs
        base.OnExit(e);
    }
}
