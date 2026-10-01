using System.Text.Json;
using LogPro.Models;
using LogPro.Helpers;

namespace LogPro.Services.Profiling;

/// <summary>Labeled device profile for tier comparisons (§12.2).</summary>
public sealed class DeviceTierProfile
{
    public string Serial { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;      // e.g. "Pixel 7"
    public string Chipset { get; init; } = string.Empty;
    public int RamMb { get; init; }
    public int RefreshRateHz { get; init; }
    public string OsVersion { get; init; } = string.Empty;
}

/// <summary>One device's summary inside a tier comparison.</summary>
public sealed class TierResult
{
    public DeviceTierProfile Profile { get; init; } = new();
    public double? AvgFps { get; init; }
    public double? MinFps { get; init; }
    public int JankyFrames { get; init; }
    public double? MaxCpuPercent { get; init; }
    public int? MemoryGrowthKb { get; init; }
    public int? BatteryDrainPercent { get; init; }
    public bool HasSufficientData { get; init; }
    public bool SlowSession { get; init; }
}

/// <summary>
/// Runs the profiler on several devices in parallel for the same duration and
/// produces a side-by-side tier comparison (§12.2).
/// </summary>
public static class TierMatrix
{
    public static async Task<IReadOnlyList<TierResult>> CompareAsync(
        IAdbService adb, IReadOnlyList<DeviceTierProfile> devices, string? package,
        TimeSpan duration, int sampleIntervalMs = 1000, CancellationToken cancellationToken = default)
    {
        var tasks = devices.Select(async profile =>
        {
            using var profiler = new AndroidPerformanceProfiler(adb, profile.Serial,
                string.IsNullOrWhiteSpace(package) ? null : package, intervalMs: sampleIntervalMs);
            profiler.Start();
            try { await Task.Delay(duration, cancellationToken); }
            finally { await profiler.StopAsync(); }
            var summary = ProfilerReportWriter.Summarize(profiler.History);
            return new TierResult
            {
                Profile = profile,
                AvgFps = summary.AvgFps,
                MinFps = summary.MinFps,
                JankyFrames = summary.JankyFrames,
                MaxCpuPercent = summary.MaxCpuPercent,
                MemoryGrowthKb = summary.MemoryGrowthKb,
                BatteryDrainPercent = summary.BatteryDrainPercent,
                SlowSession = summary.SlowSession,
                HasSufficientData = summary.HasSufficientData
            };
        });

        return await Task.WhenAll(tasks);
    }

    public static async Task WriteJsonAsync(IReadOnlyList<TierResult> results, string outputPath, TimeSpan duration)
    {
        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("GeneratedUtc", DateTime.UtcNow.ToString("O"));
        writer.WriteNumber("DurationSeconds", duration.TotalSeconds);
        writer.WritePropertyName("Devices");
        writer.WriteStartArray();
        foreach (var r in results)
        {
            writer.WriteStartObject();
            writer.WriteString("DeviceId", SecurityHelper.HashSerial(r.Profile.Serial));
            writer.WriteString("Label", r.Profile.Label == r.Profile.Serial ? SecurityHelper.HashSerial(r.Profile.Serial) : r.Profile.Label);
            writer.WriteString("Chipset", r.Profile.Chipset);
            writer.WriteNumber("RamMb", r.Profile.RamMb);
            writer.WriteNumber("RefreshRateHz", r.Profile.RefreshRateHz);
            writer.WriteString("OsVersion", r.Profile.OsVersion);
            if (r.AvgFps.HasValue) writer.WriteNumber("AvgFps", Math.Round(r.AvgFps.Value, 1));
            if (r.MinFps.HasValue) writer.WriteNumber("MinFps", Math.Round(r.MinFps.Value, 1));
            writer.WriteNumber("JankyFrames", r.JankyFrames);
            if (r.MaxCpuPercent.HasValue) writer.WriteNumber("MaxCpuPercent", Math.Round(r.MaxCpuPercent.Value, 1));
            if (r.MemoryGrowthKb.HasValue) writer.WriteNumber("MemoryGrowthKb", r.MemoryGrowthKb.Value);
            if (r.BatteryDrainPercent.HasValue) writer.WriteNumber("BatteryDrainPercent", r.BatteryDrainPercent.Value);
            writer.WriteBoolean("HasSufficientData", r.HasSufficientData);
            writer.WriteBoolean("SlowSession", r.SlowSession);
            writer.WriteString("Verdict", !r.HasSufficientData ? "INSUFFICIENT DATA" : r.SlowSession ? "SLOW SESSION" : "OK");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync();
    }
}
