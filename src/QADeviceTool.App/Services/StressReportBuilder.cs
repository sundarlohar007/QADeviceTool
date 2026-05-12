using System.Text;
using System.Text.RegularExpressions;

namespace LogPro.Services;

internal sealed class StressPerformanceMetrics
{
    public int? TotalPssKb { get; set; }
    public int? TotalRssKb { get; set; }
    public string CpuLine { get; set; } = string.Empty;
    public int? JankyFrames { get; set; }
    public int? FrameP90Ms { get; set; }
    public int? MissedVsync { get; set; }
}

internal sealed class StressRunSummary
{
    public string PackageName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public int EventsInjected { get; set; }
    public int CrashCount { get; set; }
    public int AnrCount { get; set; }
    public TimeSpan Duration { get; set; }
    public StressPerformanceMetrics Metrics { get; set; } = new();
}

internal static class StressReportBuilder
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
        var result = summary.CrashCount == 0 && summary.AnrCount == 0 ? "PASSED" : "FAILED";
        var sb = new StringBuilder();
        sb.AppendLine("========== Stress Test Report ==========");
        sb.AppendLine($"Result: {result}");
        sb.AppendLine($"Device: {summary.DeviceName}");
        sb.AppendLine($"Package: {summary.PackageName}");
        sb.AppendLine($"Duration: {summary.Duration:mm\\:ss}");
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
        sb.AppendLine("========================================");
        return sb.ToString();
    }

    private static int? MatchInt(string text, string pattern)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static string FormatKb(int? value) => value.HasValue ? $"{value.Value} KB" : "not available";
    private static string FormatMs(int? value) => value.HasValue ? $"{value.Value} ms" : "not available";
    private static string FormatInt(int? value) => value.HasValue ? value.Value.ToString() : "not available";
    private static string FormatText(string value) => string.IsNullOrWhiteSpace(value) ? "not available" : value.Trim();
}
