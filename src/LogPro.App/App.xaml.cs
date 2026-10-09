using System.IO;
using System.Windows;
using System.Windows.Threading;
using LogPro.Helpers;
using LogPro.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LogPro;

public partial class App : Application
{
    private static readonly string EarlyLogPath = Path.Combine(Helpers.PathHelper.GetAppDataDirectory(), "startup-debug.log");
    private Mutex? _runningMutex;
    private const long EarlyLogMaxBytes = 1024 * 1024; // 1 MiB cap; truncate-on-roll instead of unbounded growth.

    static App()
    {
        // Migrate before the early log creates the new directory, otherwise the presence
        // of %LOCALAPPDATA%\LogPro would intentionally suppress the legacy move.
        Helpers.PathHelper.MigrateLegacyAppData();
    }

    private void EarlyLog(string message, Exception? ex = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(EarlyLogPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            if (dir != null && !Helpers.PathHelper.RestrictDirectoryAccess(dir)) return;

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

            var logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {SecurityHelper.RedactSensitiveText(message)}\n";
            if (ex != null)
            {
                logLine += $"EXCEPTION: {ex.GetType().Name}\nMESSAGE: {SecurityHelper.RedactSensitiveText(ex.Message)}\n" +
                           $"STACK TRACE:\n{SecurityHelper.RedactSensitiveText(ex.StackTrace, redactIdentifiers: false)}\n\n";
            }
            File.AppendAllText(EarlyLogPath, logLine);
        }
        catch { /* Cannot log the logging failure */ }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        EarlyLog("========================================");
        EarlyLog("APP STARTUP ENTERED");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (await Services.UpdateBootstrap.RunAsync(e.Args)) return;
        if (Mutex.TryOpenExisting("LogProUpdating", out var updating))
        {
            updating.Dispose();
            MessageBox.Show("LogPro is installing updates. Start it again when the update finishes.", "LogPro");
            Shutdown();
            return;
        }
        // Recover before registering this instance or resolving any device executable.
        try
        {
            using var recovery = new UpdateService();
            if (recovery.HasPendingTransactions)
            {
                if (Mutex.TryOpenExisting("LogProRunning", out var running))
                { running.Dispose(); throw new InvalidOperationException("Close other LogPro windows to recover the interrupted update."); }
                if (UpdateService.RequiresElevation)
                {
                    using var helper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath!,
                        Arguments = "--recover-updates",
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                    }) ?? throw new InvalidOperationException("Could not start update recovery.");
                    await helper.WaitForExitAsync();
                    if (helper.ExitCode != 0) throw new InvalidOperationException("Interrupted update recovery failed. Repair LogPro using the Windows installer.");
                }
                else
                {
                    using var recovering = new Mutex(false, "LogProUpdating", out var created);
                    if (!created) throw new InvalidOperationException("Another update operation is running.");
                    await recovery.RecoverPendingTransactionsAsync();
                }
            }
        }
        catch (Exception ex)
        {
            EarlyLog("Startup update recovery failed", ex);
            MessageBox.Show(SecurityHelper.RedactSensitiveText(ex.Message), "LogPro recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        _runningMutex = new Mutex(false, "LogProRunning");

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

        // Verify the installed payload before services resolve any executable.
        // Never regenerate trust metadata from an unverified installation.
        try
        {
            if (!await ToolResolver.VerifyBundledToolsAsync(requireManifest: true, requireTools: false))
                EarlyLog("Bundled tool verification failed. A verified installer repair is required.");
        }
        catch (Exception ex)
        {
            EarlyLog("Bundled tool verification failed", ex);
        }

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
            MessageBox.Show("LogPro could not initialize. See startup-debug.log in the application data folder for details.", "LogPro startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        mainWindow.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        EarlyLog("Base OnStartup completed, initializing services...");

        try
        {
            // Initialize user preferences
            var prefs = Services.PreferencesService.Current;
            EarlyLog("PreferencesService initialized.");

            if (!prefs.PrivacyNoticeAccepted)
            {
                var accepted = System.Windows.MessageBox.Show(
                    "LogPro stores preferences, logs, screenshots and sessions locally. Optional update checks contact GitHub when enabled; diagnostic data is not included in those requests. Continue?",
                    "Privacy Notice", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (accepted != MessageBoxResult.Yes) { Shutdown(); return; }
                if (!PreferencesService.Update(p => p.PrivacyNoticeAccepted = true)) { Shutdown(); return; }
            }
            // Cleanup only after the first-run notice is accepted.
            Services.PreferencesService.CleanupOldLogs();
            Services.PreferencesService.CleanupOldSessions();
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

        var technicalDetails = SecurityHelper.RedactSensitiveText(e.Exception.ToString()); // full chain incl. inner exceptions (e.g. XamlParseException inner)
        var result = MessageBox.Show(
            "An unexpected error occurred. LogPro will save active captures and close.\n\nWould you like to copy technical details to clipboard?",
            "LogPro - Error",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try { Clipboard.SetText(technicalDetails); } catch { }
        }

        e.Handled = true;
        if (MainWindow != null) MainWindow.Close(); else Shutdown(1);
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
        if (MainWindow?.DataContext is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch (Exception ex) { EarlyLog("MainViewModel cleanup failed", ex); }
        }
        Services.ProcessManager.Instance.KillAllTrackedProcesses();
        _runningMutex?.Dispose();
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
            if (!Helpers.PathHelper.RestrictDirectoryAccess(dir)) return;
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
                sb.AppendLine($"[{depth}] {current.GetType().FullName}: {SecurityHelper.RedactSensitiveText(current.Message)}");
                sb.AppendLine(SecurityHelper.RedactSensitiveText(current.StackTrace, redactIdentifiers: false));
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
