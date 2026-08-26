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
                "soak" => await Soak(adb, ios, args),
                "serve" => await Serve(adb, ios, args),
                "report" => await Report(args),
                "matrix" => await Matrix(adb, args),
                "tools" => await Tools(args),
                "location" => await Location(adb, args),
                "network" => await Network(adb, args),
                "issue" => await Issue(adb, args),
                "plugins" => Plugins(args),
                "parse" => await Parse(args),
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
          profile --serial S [--seconds N] [--package P] --out DIR
                                                    Performance profiling (FPS/CPU/mem/thermal)
          soak --serial S --seconds N [--macro FILE] [--package P] --out DIR
                                                    Endurance run with decay flags
          serve [--port P]                          Loopback control API for CI/Appium
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
        Console.WriteLine($"Capturing {seconds}s from {device.DisplayName} -> {session.SessionDirectory}");
        if (!await sessions.StartCaptureAsync(session))
        {
            Console.Error.WriteLine("failed to start capture (tool missing or device offline)");
            return 1;
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        sessions.StopCapture(session);
        Console.WriteLine($"Stopped. {session.LogLineCount} lines -> {session.LogFilePath}");
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

        Console.WriteLine($"Profiling {seconds}s -> {outDir}");
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

    /// <summary>Soak/endurance run — sustained load while the profiler samples (§12.5).</summary>
    private static async Task<int> Soak(AdbService adb, IosService ios, string[] args)
    {
        var serial = Opt(args, "--serial");
        if (string.IsNullOrWhiteSpace(serial))
        {
            Console.Error.WriteLine("soak requires --serial");
            return 2;
        }

        var seconds = int.TryParse(Opt(args, "--seconds"), out var s) ? s : 600;
        var package = Opt(args, "--package") ?? string.Empty;
        var macroPath = Opt(args, "--macro");
        var outDir = Opt(args, "--out") ?? Directory.GetCurrentDirectory();

        var device = await FindDevice(adb, ios, serial);
        if (device == null)
        {
            Console.Error.WriteLine($"device not found: {serial}");
            return 1;
        }

        Directory.CreateDirectory(outDir);
        var macro = macroPath != null && File.Exists(macroPath)
            ? await MacroService.LoadMacroAsync(macroPath)
            : null;

        Func<CancellationToken, Task> load = macro != null
            ? async token => await new MacroService(adb).ReplayMacroAsync(serial, macro, token: token)
            : async token =>
            {
                var i = 0;
                while (!token.IsCancellationRequested)
                {
                    await adb.ExecuteCommandAsync(serial, $"shell input keyevent {82 + (i++ % 4)}");
                    await Task.Delay(200, token);
                }
            };

        Console.WriteLine($"Soaking {seconds}s on {device.DisplayName} -> {outDir}");
        var duration = TimeSpan.FromSeconds(seconds);
        var report = await LogPro.Services.Profiling.SoakRunner.RunAsync(adb, serial, package, duration, load);

        Console.WriteLine($"Samples: {report.SampleCount} | FPS start/end: {report.AvgFpsStart?.ToString("F1") ?? "n/a"} / {report.AvgFpsEnd?.ToString("F1") ?? "n/a"} | decay: {report.FpsDecay?.ToString("F1") ?? "n/a"}");
        Console.WriteLine($"Memory growth: {report.MemoryGrowthKb / 1024} MB | Janky: {report.JankyFrames} | Max thermal: {report.MaxThermalStatus}");
        Console.WriteLine($"Flags: mem={(report.MemoryGrowthFlagged ? "YES" : "no")} fpsDecay={(report.FpsDecayFlagged ? "YES" : "no")} thermal={(report.ThermalFlagged ? "YES" : "no")}");
        Console.WriteLine(report.HasIssues ? "RESULT: ISSUES DETECTED" : "RESULT: PASS");

        var summary = LogPro.Services.Profiling.ProfilerReportWriter.Summarize(new List<LogPro.Services.Profiling.ProfilerSnapshot>());
        return report.SampleCount > 0 ? 0 : 1;
    }

    /// <summary>Runs the loopback control API (§16) for CI/Appium harnesses.</summary>
    private static async Task<int> Serve(AdbService adb, IosService ios, string[] args)
    {
        var port = int.TryParse(Opt(args, "--port"), out var p) ? p : 8417;
        using var server = new ControlApiServer(adb, ios);
        server.Start(port);
        Console.WriteLine($"Control API listening on http://127.0.0.1:{port} (Ctrl+C to stop)");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    /// <summary>Renders an HTML session report from a profile-report.json (§12.9).</summary>
    private static async Task<int> Report(string[] args)
    {
        var jsonPath = Opt(args, "--json");
        var outPath = Opt(args, "--out");
        if (string.IsNullOrWhiteSpace(jsonPath) || string.IsNullOrWhiteSpace(outPath))
        {
            Console.Error.WriteLine("report requires --json and --out");
            return 2;
        }
        if (!File.Exists(jsonPath))
        {
            Console.Error.WriteLine($"file not found: {jsonPath}");
            return 1;
        }

        var snapshots = new List<LogPro.Services.Profiling.ProfilerSnapshot>();
        using (var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath)))
        {
            if (doc.RootElement.TryGetProperty("Samples", out var samples))
            {
                foreach (var s in samples.EnumerateArray())
                {
                    snapshots.Add(new LogPro.Services.Profiling.ProfilerSnapshot
                    {
                        Timestamp = ReadDate(s, "Timestamp"),
                        Fps = ReadDouble(s, "Fps"),
                        FrameTimeP90Ms = ReadDouble(s, "FrameTimeP90Ms"),
                        CpuPercent = ReadDouble(s, "CpuPercent"),
                        PssKb = ReadInt(s, "PssKb"),
                        JankyFrames = ReadInt(s, "JankyFrames"),
                        ThermalStatus = ReadInt(s, "ThermalStatus"),
                        BatteryLevel = ReadInt(s, "BatteryLevel")
                    });
                }
            }
        }

        var title = Opt(args, "--title") ?? $"LogPro Session Report";
        var html = LogPro.Services.Profiling.ProfilerReportHtml.Render(title, snapshots);
        await File.WriteAllTextAsync(outPath, html);
        Console.WriteLine($"Report written -> {outPath} ({snapshots.Count} samples)");
        return 0;

        static DateTime ReadDate(System.Text.Json.JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && DateTime.TryParse(v.GetString(), out var d) ? d : DateTime.UtcNow;
        static double? ReadDouble(System.Text.Json.JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : null;
        static int? ReadInt(System.Text.Json.JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : null;
    }

    /// <summary>Device-tier matrix — profiles several devices in parallel and compares (§12.2).</summary>
    private static async Task<int> Matrix(AdbService adb, string[] args)
    {
        var serialsArg = Opt(args, "--serials");
        if (string.IsNullOrWhiteSpace(serialsArg))
        {
            Console.Error.WriteLine("matrix requires --serials A,B,C");
            return 2;
        }

        var seconds = int.TryParse(Opt(args, "--seconds"), out var s) ? s : 60;
        var package = Opt(args, "--package");
        var outDir = Opt(args, "--out") ?? Directory.GetCurrentDirectory();
        var labels = (Opt(args, "--labels") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var chipsets = (Opt(args, "--chipsets") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);

        var serials = serialsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var profiles = serials.Select((serial, i) => new LogPro.Services.Profiling.DeviceTierProfile
        {
            Serial = serial,
            Label = i < labels.Length ? labels[i] : serial,
            Chipset = i < chipsets.Length ? chipsets[i] : string.Empty
        }).ToList();

        Directory.CreateDirectory(outDir);
        var deviceCount = serials.Length;
        Console.WriteLine("Comparing " + deviceCount + " devices for " + seconds + "s -> " + outDir);

        var results = await LogPro.Services.Profiling.TierMatrix.CompareAsync(
            adb, profiles, package, TimeSpan.FromSeconds(seconds));

        var jsonPath = Path.Combine(outDir, "tier-comparison.json");
        await LogPro.Services.Profiling.TierMatrix.WriteJsonAsync(results, jsonPath);

        Console.WriteLine($"{"Device",-14} {"Label",-12} {"AvgFPS",8} {"MinFPS",8} {"Jank",6} {"MaxCPU",8} {"MemGrw",8} {"Slow",6}");
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Profile.Serial,-14} {r.Profile.Label,-12} {(r.AvgFps?.ToString("F1") ?? "n/a"),8} {(r.MinFps?.ToString("F1") ?? "n/a"),8} {r.JankyFrames,6} {(r.MaxCpuPercent?.ToString("F0") ?? "n/a"),8} {$"{r.MemoryGrowthKb / 1024} MB",8} {(r.SlowSession ? "YES" : "no"),6}");
        }
        Console.WriteLine($"Report: {jsonPath}");
        return results.Count > 0 ? 0 : 1;
    }

    /// <summary>Bundled-tool integrity (§7.1): write or verify the sha256 manifest.</summary>
    private static async Task<int> Tools(string[] args)
    {
        var sub = args.Length > 1 ? args[1] : "verify";
        var toolsRoot = LogPro.Helpers.ToolLauncher.ToolsDirectory;
        if (!Directory.Exists(toolsRoot))
        {
            Console.Error.WriteLine($"no bundled tools directory: {toolsRoot}");
            return 1;
        }

        var manifestPath = Opt(args, "--manifest") ?? Path.Combine(toolsRoot, LogPro.Services.ToolManifest.DefaultFileName);

        if (sub == "manifest")
        {
            await LogPro.Services.ToolManifest.WriteAsync(toolsRoot, manifestPath);
            Console.WriteLine($"Manifest written → {manifestPath}");
            return 0;
        }

        var result = await LogPro.Services.ToolManifest.VerifyAsync(toolsRoot, manifestPath);
        Console.WriteLine($"Tools root: {toolsRoot}");
        Console.WriteLine($"OK: {result.Ok.Count} | mismatched: {result.Mismatched.Count} | missing: {result.Missing.Count} | unexpected: {result.Unexpected.Count}");
        foreach (var m in result.Mismatched) Console.WriteLine($"  MISMATCH {m.Path} (expected {m.Sha256[..12]}…)");
        foreach (var m in result.Missing) Console.WriteLine($"  MISSING  {m}");
        foreach (var u in result.Unexpected) Console.WriteLine($"  UNEXPECTED {u}");
        return result.IsHealthy ? 0 : 1;
    }

    /// <summary>Mock-location simulation (§12.4) with a mandatory reset.</summary>
    private static async Task<int> Location(AdbService adb, string[] args)
    {
        var sub = args.Length > 1 ? args[1] : "";
        var serial = Opt(args, "--serial");
        var app = Opt(args, "--app");
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(app))
        {
            Console.Error.WriteLine("location requires --serial and --app");
            return 2;
        }

        var sim = new LogPro.Services.ConditionSimulator(adb);

        if (sub == "reset")
        {
            var ok = await sim.ResetLocationAsync(serial, app);
            Console.WriteLine(ok ? $"Mock location revoked from {app}." : "Reset failed.");
            return ok ? 0 : 1;
        }

        if (sub == "route")
        {
            var waypointsArg = Opt(args, "--waypoints");
            var speed = double.TryParse(Opt(args, "--speed"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var spd) ? spd : 5.0;
            var seconds = int.TryParse(Opt(args, "--seconds"), out var secs) ? secs : 60;
            if (string.IsNullOrWhiteSpace(waypointsArg))
            {
                Console.Error.WriteLine("route requires --waypoints \"lat,lon;lat,lon\"");
                return 2;
            }

            var waypoints = waypointsArg.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => w.Split(',', StringSplitOptions.TrimEntries))
                .Select(p => (double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                              double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))
                .ToList();

            await sim.SetMockLocationAppAsync(serial, app);
            var fixes = LogPro.Services.ConditionPlanners.PlanRoute(waypoints, speed, TimeSpan.FromSeconds(seconds));
            Console.WriteLine($"Injecting {fixes.Count} fixes at {speed} m/s for {seconds}s (mock provider must be running on-device)");
            foreach (var fix in fixes)
            {
                await sim.InjectFixAsync(serial, fix.Latitude, fix.Longitude);
                await Task.Delay(1000);
            }
            Console.WriteLine("Route complete. Run 'logpro-cli location reset --serial " + serial + " --app " + app + "' to revoke mock location.");
            return 0;
        }

        Console.Error.WriteLine("usage: location route|reset --serial S --app P [...]");
        return 2;
    }

    /// <summary>Network conditioning via root tc/netem (§12.3).</summary>
    private static async Task<int> Network(AdbService adb, string[] args)
    {
        var sub = args.Length > 1 ? args[1] : "";
        var serial = Opt(args, "--serial");
        var iface = Opt(args, "--interface") ?? "wlan0";
        if (string.IsNullOrWhiteSpace(serial))
        {
            Console.Error.WriteLine("network requires --serial");
            return 2;
        }

        var sim = new LogPro.Services.ConditionSimulator(adb);

        if (sub == "reset")
        {
            var ok = await sim.ResetNetworkConditionAsync(serial, iface);
            Console.WriteLine(ok ? $"Network conditioning reset on {iface}." : "Reset failed.");
            return ok ? 0 : 1;
        }

        if (sub == "apply")
        {
            var presetName = Opt(args, "--preset") ?? "4g";
            var preset = LogPro.Services.ConditionPlanners.Presets.FirstOrDefault(p =>
                p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
            {
                Console.Error.WriteLine($"unknown preset '{presetName}' — use: {string.Join(", ", LogPro.Services.ConditionPlanners.Presets.Select(p => p.Name))}");
                return 2;
            }

            var ok = await sim.ApplyNetworkConditionAsync(serial, preset, iface);
            Console.WriteLine(ok
                ? $"Applied {preset.Name}: {preset.LatencyMs}ms±{preset.JitterMs}ms, {preset.LossPercent}% loss, {preset.BandwidthMbps} Mbps on {iface}"
                : "Failed — device needs root (su) for tc/netem conditioning.");
            return ok ? 0 : 1;
        }

        Console.Error.WriteLine("usage: network apply|reset --serial S [--preset 3g|4g|5g|edge|metro] [--interface wlan0]");
        return 2;
    }

    /// <summary>Exports a REDACTED issue bundle (markdown + evidence) to disk — no network (privacy hard gate).</summary>
    private static async Task<int> Issue(AdbService adb, string[] args)
    {
        var serial = Opt(args, "--serial");
        var outDir = Opt(args, "--out") ?? Directory.GetCurrentDirectory();
        var sessionDir = Opt(args, "--session-dir");
        var title = Opt(args, "--title");
        var attachmentsArg = Opt(args, "--attachments");

        LogPro.Models.DeviceInfo? device = null;
        if (!string.IsNullOrWhiteSpace(serial))
        {
            var ios = new IosService();
            device = await FindDevice(adb, ios, serial);
            if (device == null)
            {
                Console.Error.WriteLine($"device not found: {serial}");
                return 1;
            }
        }

        var attachments = new List<string>();
        if (!string.IsNullOrWhiteSpace(attachmentsArg))
            attachments.AddRange(attachmentsArg.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var logFile = sessionDir != null
            ? Directory.GetFiles(sessionDir, "*_log.txt", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        var bundle = await LogPro.Services.IssueExportService.ExportAsync(new LogPro.Services.IssueExportRequest
        {
            Device = device,
            Title = title,
            SessionLogFilePath = logFile,
            Attachments = attachments,
            OutputDirectory = outDir
        });

        Console.WriteLine($"Issue bundle → {bundle.DirectoryPath}");
        foreach (var f in bundle.Files) Console.WriteLine($"  {Path.GetFileName(f)}");
        Console.WriteLine("Attach these files manually in your tracker — the tool never transmits anything.");
        return 0;
    }

    /// <summary>Lists discovered plugins (§16).</summary>
    private static int Plugins(string[] args)
    {
        var dir = Opt(args, "--dir") ?? Path.Combine(Directory.GetCurrentDirectory(), "plugins");
        var manager = new LogPro.Services.Plugins.PluginManager();
        manager.LoadPlugins(dir);

        Console.WriteLine($"Plugins from {dir}:");
        foreach (var plugin in manager.Plugins)
            Console.WriteLine($"  {plugin.Id} v{plugin.Version} [{plugin.Type}] — {plugin.Name}");
        Console.WriteLine($"{manager.Plugins.Count} plugin(s) loaded.");
        return 0;
    }

    /// <summary>Applies a parser plugin to a log file.</summary>
    private static async Task<int> Parse(string[] args)
    {
        var dir = Opt(args, "--plugins-dir");
        var parserId = Opt(args, "--parser");
        var input = Opt(args, "--input");
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(parserId) || string.IsNullOrWhiteSpace(input))
        {
            Console.Error.WriteLine("parse requires --plugins-dir, --parser and --input");
            return 2;
        }
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"file not found: {input}");
            return 1;
        }

        var manager = new LogPro.Services.Plugins.PluginManager();
        manager.LoadPlugins(dir);
        if (!manager.LogParsers.TryGetValue(parserId, out var parser))
        {
            Console.Error.WriteLine($"parser not found: {parserId}");
            return 1;
        }

        var counts = new Dictionary<string, int>();
        var parsed = 0;
        await foreach (var line in System.IO.File.ReadLinesAsync(input))
        {
            if (!parser.TryParse(line, out var entry)) continue;
            parsed++;
            counts[entry.Level] = counts.GetValueOrDefault(entry.Level) + 1;
        }

        Console.WriteLine($"Parsed {parsed} line(s) with '{parserId}':");
        foreach (var (level, count) in counts.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {level,-10} {count}");
        return 0;
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

        Console.WriteLine(ok ? $"Exported -> {outPath}" : "export failed");
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
