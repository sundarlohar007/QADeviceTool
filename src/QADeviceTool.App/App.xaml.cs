using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace QADeviceTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Global exception handlers to prevent crashes
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        
        // Ensure sessions directory exists
        try { Helpers.PathHelper.EnsureSessionsDirectory(); } catch { }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("DispatcherUnhandled", e.Exception);
        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}\n\nDetails written to crash.log",
            "QA/QC Device Tool - Error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true; // Prevent crash — keep app running
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogError("AppDomainUnhandled", ex);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogError("TaskUnobserved", e.Exception);
        e.SetObserved(); // Prevent crash
    }

    private static void LogError(string context, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}]\n{ex}\n{"".PadRight(80, '=')}\n\n";
            File.AppendAllText(logPath, entry);
        }
        catch { }
    }
}
