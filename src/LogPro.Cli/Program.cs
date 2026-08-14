using LogPro.Models;
using LogPro.Services;

namespace LogPro.Cli;

/// <summary>
/// Headless CLI for the LogPro engine. Commands:
///   devices                          List connected devices (Android + iOS)
///   capture --serial X [--seconds N] [--out DIR] [--package P]
///   export  --log FILE --format csv|json --out FILE [--anonymize]
///   bugreport --serial X [--out DIR]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        _ = AppLogger.Log; // force NLog config init before we strip the console target
        QuietConsoleLogging();

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        var adb = new AdbService();
        var ios = new IosService();

        try
        {
            return args[0] switch
            {
                "devices" => await ListDevices(adb, ios),
                "capture" => await Capture(adb, ios, args),
                "profile" => await Profile(adb, ios, args),
                "export" => await Export(adb, ios, args),
                "bugreport" => await BugReport(adb, ios, args),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    /// <summary>CLI stdout belongs to command output — file logging stays, console target goes quiet.</summary>
    private static void QuietConsoleLogging()
    {
        var config = NLog.LogManager.Configuration;
        if (config == null) return;
        foreach (var rule in config.LoggingRules.ToList())
        {
            for (var i = rule.Targets.Count - 1; i >= 0; i--)
            {
                if (rule.Targets[i] is NLog.Targets.ConsoleTarget)
                    rule.Targets.RemoveAt(i);
            }
        }
        NLog.LogManager.ReconfigExistingLoggers();
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage() => Console.WriteLine("""
        logpro — headless device tooling (LogPro engine)

          devices                                   List connected devices
          capture --serial S [--seconds N] [--out DIR] [--package P]
                                                    Capture device logs (default 30s)
          export --log FILE --format csv|json --out FILE [--anonymize]
                                                    Export a session log file
          bugreport --serial S [--out DIR]          Generate a zipped bug report
        """);

    private static string? Opt(string[] args, string name)
    {
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static async Task<int> ListDevices(AdbService adb, IosService ios)
    {
        var android = await adb.GetConnectedDevicesAsync();
        var apple = await ios.GetConnectedDevicesAsync();
        var all = android.Concat(apple).ToList();

        if (all.Count == 0)
        {
            Console.WriteLine("No devices connected.");
            return 1;
        }

        foreach (var d in all)
            Console.WriteLine($"{d.Platform,-8} {d.Serial,-24} {d.ConnectionState,-14} {d.DisplayName}");
        return 0;
    }

    private static async Task<int> Capture(AdbService adb, IosService ios, string[] args)
    {
        var serial = Opt(args, "--serial");
        if (string.IsNullOrWhiteSpace(serial))
        {
            Console.Error.WriteLine("capture requires --serial");
            return 2;
        }

        var seconds = int.TryParse(Opt(args, "--seconds"), out var s) ? s : 30;
        var outDir = Opt(args, "--out");
        var package = Opt(args, "--package");

        var device = await FindDevice(adb, ios, serial);
        if (device == null)
        {
            Console.Error.WriteLine($"device not found: {serial}");
            return 1;
        }

        var sessions = new SessionService(adb, ios);
        if (!string.IsNullOrWhiteSpace(outDir))
            sessions.SessionsRootDirectory = outDir;
        if (!string.IsNullOrWhiteSpace(package))
            PreferencesService.Current.TargetPackageName = package;

        var session = sessions.CreateSession(device);
        Console.WriteLine($"Capturing {seconds}s from {device.DisplayName} → {session.SessionDirectory}");
        if (!await sessions.StartCaptureAsync(session))
        {
            Console.Error.WriteLine("failed to start capture (tool missing or device offline)");
            return 1;
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        sessions.StopCapture(session);
        Console.WriteLine($"Stopped. {session.LogLineCount} lines → {session.LogFilePath}");
        return 0;
    }

    private static async Task<int> Profile(AdbService adb, IosService ios, string[] args)
    {
        var serial = Opt(args, "--serial");
        if (string.IsNullOrWhiteSpace(serial))
        {
            Console.Error.WriteLine("profile requires --serial");
            return 2;
        }

        var seconds = int.TryParse(Opt(args, "--seconds"), out var s) ? s : 30;
        var package = Opt(args, "--package");
        var layer = Opt(args, "--layer");
        var outDir = Opt(args, "--out") ?? Directory.GetCurrentDirectory();

        var device = await FindDevice(adb, ios, serial);
        if (device == null)
        {
            Console.Error.WriteLine($"device not found: {serial}");
            return 1;
        }

        Directory.CreateDirectory(outDir);
        using var profiler = new LogPro.Services.Profiling.AndroidPerformanceProfiler(
            adb, serial, package, layer, intervalMs: 1000);

        Console.WriteLine($"Profiling {seconds}s → {outDir}");
        profiler.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await profiler.StopAsync();

        var history = profiler.History;
        var jsonPath = Path.Combine(outDir, "profile-report.json");
        var csvPath = Path.Combine(outDir, "profile.csv");
        await LogPro.Services.Profiling.ProfilerReportWriter.WriteJsonAsync(history, jsonPath);
        await LogPro.Services.Profiling.ProfilerReportWriter.WriteCsvAsync(history, csvPath);

        var sum = LogPro.Services.Profiling.ProfilerReportWriter.Summarize(history);
        Console.WriteLine($"Samples: {history.Count} | Avg FPS: {sum.AvgFps?.ToString("F1") ?? "n/a"} | Janky: {sum.JankyFrames} | Max CPU: {sum.MaxCpuPercent?.ToString("F0") ?? "n/a"}% | Mem growth: {sum.MemoryGrowthKb / 1024} MB | Slow session: {sum.SlowSession}");
        Console.WriteLine($"Report: {jsonPath}");
        return history.Count > 0 ? 0 : 1;
    }

    private static async Task<int> Export(AdbService adb, IosService ios, string[] args)
    {
        var log = Opt(args, "--log");
        var format = Opt(args, "--format") ?? "csv";
        var outPath = Opt(args, "--out");
        var anonymize = args.Contains("--anonymize");

        if (string.IsNullOrWhiteSpace(log) || string.IsNullOrWhiteSpace(outPath))
        {
            Console.Error.WriteLine("export requires --log and --out");
            return 2;
        }

        var sessions = new SessionService(adb, ios);
        var session = new LogSession { LogFilePath = log, SessionDirectory = Path.GetDirectoryName(log) ?? "" };
        var ok = format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? await sessions.ExportToJsonAsync(session, outPath, anonymize)
            : await sessions.ExportToCsvAsync(session, outPath, anonymize);

        Console.WriteLine(ok ? $"Exported → {outPath}" : "export failed");
        return ok ? 0 : 1;
    }

    private static async Task<int> BugReport(AdbService adb, IosService ios, string[] args)
    {
        var serial = Opt(args, "--serial");
        if (string.IsNullOrWhiteSpace(serial))
        {
            Console.Error.WriteLine("bugreport requires --serial");
            return 2;
        }

        var device = await FindDevice(adb, ios, serial);
        if (device == null)
        {
            Console.Error.WriteLine($"device not found: {serial}");
            return 1;
        }

        var outDir = Opt(args, "--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "bugreports");
        var reporter = new BugReportService(adb, ios);
        var (success, message) = await reporter.GenerateAsync(
            device, outDir, "cli", Array.Empty<string>(), Array.Empty<CrashDetector.CrashEvent>(), null);
        Console.WriteLine(message);
        return success ? 0 : 1;
    }

    private static async Task<DeviceInfo?> FindDevice(AdbService adb, IosService ios, string serial)
    {
        var android = await adb.GetConnectedDevicesAsync();
        var apple = await ios.GetConnectedDevicesAsync();
        return android.Concat(apple).FirstOrDefault(d =>
            d.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase));
    }
}
