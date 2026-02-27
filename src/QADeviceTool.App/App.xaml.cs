using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace QADeviceTool;

public partial class App : Application
{
    private static readonly string EarlyLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QAQCDeviceTool", "startup-debug.log");

    private void EarlyLog(string message, Exception? ex = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(EarlyLogPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

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
        EarlyLog($"Base Directory: {AppContext.BaseDirectory}");

        // Inject tools directory directly into PATH securely
        string toolsDir = Path.Combine(AppContext.BaseDirectory, "tools", "iMobileDevice");
        Environment.SetEnvironmentVariable(
            "PATH",
            toolsDir + ";" + Environment.GetEnvironmentVariable("PATH"),
            EnvironmentVariableTarget.Process);
            
        EarlyLog($"Tools Directory: {toolsDir} injected into PATH");

        base.OnStartup(e);

        EarlyLog("Base OnStartup completed, initializing services...");

        try
        {
            // Initialize user preferences
            var prefs = Services.PreferencesService.Current;
            EarlyLog("PreferencesService initialized.");

            Services.AppLogger.Log.Info("========================================");
            Services.AppLogger.Log.Info("QA/QC Device Tool - Application Starting");
            Services.AppLogger.Log.Info("========================================");

            // Global exception handlers to prevent crashes and ensure they are captured in early logs
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

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
            "QA/QC Device Tool - Error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
            
        e.Handled = true; // Prevent crash — keep app running
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
