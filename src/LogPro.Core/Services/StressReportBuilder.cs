using System.Text;
using System.Text.RegularExpressions;

namespace LogPro.Services;

public sealed class MetricSnapshot
{
    public DateTime Timestamp { get; set; }
    public int? TotalPssKb { get; set; }
    public double? CpuPercent { get; set; }
    public double? Fps { get; set; }
    public int? ThermalStatus { get; set; }
    public int EventsInjected { get; set; }
}

public sealed class StressPerformanceMetrics
{
    public int? TotalPssKb { get; set; }
    public int? TotalRssKb { get; set; }
    public string CpuLine { get; set; } = string.Empty;
    public int? JankyFrames { get; set; }
    public int? FrameP90Ms { get; set; }
    public int? MissedVsync { get; set; }
}

public sealed class StressRunSummary
{
    public string PackageName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public int RequestedEventCount { get; set; }
    public int CycleCount { get; set; }
    public int EventsInjected { get; set; }
    public int CrashCount { get; set; }
    public int AnrCount { get; set; }
    public TimeSpan Duration { get; set; }
    public List<MetricSnapshot> MetricSnapshots { get; set; } = new();
    public StressPerformanceMetrics Metrics { get; set; } = new();
    public string Outcome { get; set; } = "Completed";
    public string RunMode { get; set; } = "Events";
    public int? ExitCode { get; set; }
    public int Seed { get; set; }
    public int ThrottleMs { get; set; }
    public int PctTouch { get; set; }
    public int PctMotion { get; set; }
    public int PctTrackball { get; set; }
    public int PctNav { get; set; }
    public int PctSyskeys { get; set; }
    public int PctAppswitch { get; set; }
    public DateTime StartedUtc { get; set; }
    public string DeviceSerialHash { get; set; } = string.Empty;
    public string? FailureReason { get; set; }

    public string Result => CrashCount > 0 || AnrCount > 0 ? "FAILED" :
        Outcome == "Cancelled" ? "CANCELLED" :
        Outcome == "Error" ? "ERROR" :
        Outcome == "DurationComplete" ? EventsInjected > 0 ? "PASSED" : "INCOMPLETE" :
        ExitCode is > 0 or < 0 ? "ERROR" :
        EventsInjected < EventCount ? "INCOMPLETE" : "PASSED";
}

public static class StressReportBuilder
{
    public static StressPerformanceMetrics ParseMetrics(string packageName, string meminfo, string cpuinfo, string gfxinfo)
    {
        var metrics = new StressPerformanceMetrics
        {
            TotalPssKb = MatchInt(meminfo, @"TOTAL\s+PSS:\s*(\d+)")
                ?? MatchInt(meminfo, @"(?m)^\s*TOTAL\s+(\d+)"),
            TotalRssKb = MatchInt(meminfo, @"TOTAL\s+RSS:\s*(\d+)"),
            JankyFrames = MatchInt(gfxinfo, @"Janky frames:\s*(\d+)"),
            FrameP90Ms = MatchInt(gfxinfo, @"90th percentile:\s*(\d+)ms"),
            MissedVsync = MatchInt(gfxinfo, @"Number Missed Vsync:\s*(\d+)")
        };

        metrics.CpuLine = cpuinfo
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains(packageName, StringComparison.OrdinalIgnoreCase) && l.Contains('%'))
            ?? string.Empty;

        return metrics;
    }

    public static string BuildReport(StressRunSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== Stress Test Report ==========");
        sb.AppendLine($"Result: {summary.Result}");
        sb.AppendLine($"Device: {summary.DeviceName}");
        if (!string.IsNullOrEmpty(summary.DeviceSerialHash)) sb.AppendLine($"Device ID hash: {summary.DeviceSerialHash}");
        sb.AppendLine($"Package: {summary.PackageName}");
        sb.AppendLine($"Mode: {summary.RunMode}");
        if (summary.RunMode == "Duration")
            sb.AppendLine($"Cycles: {summary.CycleCount}  Events per cycle: {summary.RequestedEventCount}");
        sb.AppendLine($"Duration: {(int)summary.Duration.TotalHours:D2}:{summary.Duration.Minutes:D2}:{summary.Duration.Seconds:D2}");
        sb.AppendLine($"Seed: {summary.Seed}  Throttle: {summary.ThrottleMs}ms");
        sb.AppendLine($"Mix %: touch {summary.PctTouch}, motion {summary.PctMotion}, trackball {summary.PctTrackball}, nav {summary.PctNav}, system keys {summary.PctSyskeys}, app switch {summary.PctAppswitch}");
        if (summary.ExitCode.HasValue) sb.AppendLine($"ADB exit code: {summary.ExitCode.Value}");
        if (!string.IsNullOrWhiteSpace(summary.FailureReason)) sb.AppendLine($"Reason: {summary.FailureReason}");
        sb.AppendLine($"Events: {summary.EventsInjected}/{summary.EventCount}");
        sb.AppendLine($"Crashes: {summary.CrashCount}");
        sb.AppendLine($"ANRs: {summary.AnrCount}");
        sb.AppendLine();
        sb.AppendLine("Performance Snapshot");
        sb.AppendLine($"Memory PSS: {FormatKb(summary.Metrics.TotalPssKb)}");
        sb.AppendLine($"Memory RSS: {FormatKb(summary.Metrics.TotalRssKb)}");
        sb.AppendLine($"CPU: {FormatText(summary.Metrics.CpuLine)}");
        sb.AppendLine($"Janky Frames: {FormatInt(summary.Metrics.JankyFrames)}");
        sb.AppendLine($"Frame P90: {FormatMs(summary.Metrics.FrameP90Ms)}");
        sb.AppendLine($"Missed Vsync: {FormatInt(summary.Metrics.MissedVsync)}");
        if (summary.MetricSnapshots.Count > 0)
        {
            var validMemory = summary.MetricSnapshots.Where(m => m.TotalPssKb.HasValue).Select(m => m.TotalPssKb!.Value).ToList();
            var validCpu = summary.MetricSnapshots.Where(m => m.CpuPercent.HasValue).Select(m => m.CpuPercent!.Value).ToList();
            var validFps = summary.MetricSnapshots.Where(m => m.Fps.HasValue).Select(m => m.Fps!.Value).ToList();
            sb.AppendLine($"Samples: {summary.MetricSnapshots.Count}");
            if (validMemory.Count > 0) sb.AppendLine($"Memory range: {validMemory.Min() / 1024}–{validMemory.Max() / 1024} MB");
            if (validCpu.Count > 0) sb.AppendLine($"Average CPU: {validCpu.Average():F1}%");
            if (validFps.Count > 0) sb.AppendLine($"Average FPS: {validFps.Average():F1}");
        }
        sb.AppendLine("========================================");
        return sb.ToString();
    }

    private static int? MatchInt(string text, string pattern)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var value))
        {
            return value;
        }

        AppLogger.Log.Warn($"[StressReportBuilder] Failed to match pattern '{pattern}' in metrics output.");
        return null;
    }

    private static string FormatKb(int? value) => value.HasValue ? $"{value.Value} KB" : "not available";
    private static string FormatMs(int? value) => value.HasValue ? $"{value.Value} ms" : "not available";
    private static string FormatInt(int? value) => value.HasValue ? value.Value.ToString() : "not available";
    private static string FormatText(string value) => string.IsNullOrWhiteSpace(value) ? "not available" : value.Trim();

}

