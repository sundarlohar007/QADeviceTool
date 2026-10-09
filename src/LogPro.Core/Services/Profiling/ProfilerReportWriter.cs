using System.Text.Json;

namespace LogPro.Services.Profiling;

/// <summary>Writes bounded profiler runs to JSON or CSV without constructing a second full output document.</summary>
public static class ProfilerReportWriter
{
    public static async Task WriteJsonAsync(IReadOnlyList<ProfilerSnapshot> snapshots, string outputPath, IReadOnlyList<ProfilerMarker>? markers = null, ProfilerSummary? fullRunSummary = null, int? totalSampleCount = null)
    {
        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("GeneratedUtc", DateTime.UtcNow.ToString("O"));
        writer.WriteNumber("SampleCount", snapshots.Count);
        writer.WriteNumber("TotalRunSampleCount", totalSampleCount ?? snapshots.Count);
        var summary = fullRunSummary ?? Summarize(snapshots);
        writer.WritePropertyName("Summary");
        writer.WriteStartObject();
        if (summary.AvgFps.HasValue) writer.WriteNumber("AvgFps", summary.AvgFps.Value); else writer.WriteNull("AvgFps");
        if (summary.MinFps.HasValue) writer.WriteNumber("MinFps", summary.MinFps.Value); else writer.WriteNull("MinFps");
        writer.WriteNumber("JankyFrames", summary.JankyFrames);
        if (summary.MaxCpuPercent.HasValue) writer.WriteNumber("MaxCpuPercent", summary.MaxCpuPercent.Value); else writer.WriteNull("MaxCpuPercent");
        if (summary.MemoryGrowthKb.HasValue) writer.WriteNumber("MemoryGrowthKb", summary.MemoryGrowthKb.Value);
        if (summary.BatteryDrainPercent.HasValue) writer.WriteNumber("BatteryDrainPercent", summary.BatteryDrainPercent.Value);
        if (summary.MaxThermalStatus.HasValue) writer.WriteNumber("MaxThermalStatus", summary.MaxThermalStatus.Value);
        writer.WriteBoolean("HasSufficientData", summary.HasSufficientData);
        writer.WriteString("Verdict", summary.Verdict);
        writer.WriteNumber("FpsSampleCount", summary.FpsSampleCount);
        writer.WriteNumber("CpuSampleCount", summary.CpuSampleCount);
        writer.WriteNumber("MemorySampleCount", summary.MemorySampleCount);
        writer.WriteBoolean("SlowSession", summary.SlowSession);
        writer.WriteEndObject();

        writer.WritePropertyName("Samples");
        writer.WriteStartArray();
        foreach (var s in snapshots)
        {
            writer.WriteStartObject();
            writer.WriteString("Timestamp", s.Timestamp.ToString("O"));
            if (s.Fps.HasValue) writer.WriteNumber("Fps", Math.Round(s.Fps.Value, 1));
            if (s.FrameTimeP90Ms.HasValue) writer.WriteNumber("FrameTimeP90Ms", Math.Round(s.FrameTimeP90Ms.Value, 2));
            if (s.FrameTimeP95Ms.HasValue) writer.WriteNumber("FrameTimeP95Ms", Math.Round(s.FrameTimeP95Ms.Value, 2));
            if (s.JankyFrames.HasValue) writer.WriteNumber("JankyFrames", s.JankyFrames.Value);
            if (s.TotalFrames.HasValue) writer.WriteNumber("TotalFrames", s.TotalFrames.Value);
            if (s.CpuPercent.HasValue) writer.WriteNumber("CpuPercent", Math.Round(s.CpuPercent.Value, 1));
            if (s.PssKb.HasValue) writer.WriteNumber("PssKb", s.PssKb.Value);
            if (s.RssKb.HasValue) writer.WriteNumber("RssKb", s.RssKb.Value);
            if (s.ThermalStatus.HasValue) writer.WriteNumber("ThermalStatus", s.ThermalStatus.Value);
            if (s.BatteryLevel.HasValue) writer.WriteNumber("BatteryLevel", s.BatteryLevel.Value);
            writer.WriteBoolean("ThermalFresh", s.ThermalFresh);
            writer.WriteBoolean("BatteryFresh", s.BatteryFresh);
            writer.WriteNumber("SampleDurationMs", Math.Round(s.SampleDurationMs, 1));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("Markers");
        writer.WriteStartArray();
        foreach (var marker in markers ?? Array.Empty<ProfilerMarker>())
        {
            writer.WriteStartObject();
            writer.WriteString("Timestamp", marker.Timestamp.ToString("O"));
            writer.WriteString("Label", marker.Label);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync();
    }

    public static async Task WriteCsvAsync(IReadOnlyList<ProfilerSnapshot> snapshots, string outputPath, IReadOnlyList<ProfilerMarker>? markers = null)
    {
        await using var writer = new StreamWriter(outputPath, false);
        await writer.WriteLineAsync("Timestamp,Fps,FrameTimeP90Ms,FrameTimeP95Ms,JankyFrames,TotalFrames,CpuPercent,PssKb,RssKb,ThermalStatus,BatteryLevel,ThermalFresh,BatteryFresh,SampleDurationMs,Event");
        var orderedMarkers = (markers ?? Array.Empty<ProfilerMarker>()).OrderBy(m => m.Timestamp).ToArray();
        var markerIndex = 0;
        foreach (var s in snapshots)
        {
            while (markerIndex < orderedMarkers.Length && orderedMarkers[markerIndex].Timestamp <= s.Timestamp)
                await WriteMarkerAsync(orderedMarkers[markerIndex++]);
            await writer.WriteLineAsync(string.Join(',',
                s.Timestamp.ToString("O"),
                Fmt(s.Fps, 1), Fmt(s.FrameTimeP90Ms, 2), Fmt(s.FrameTimeP95Ms, 2),
                s.JankyFrames?.ToString() ?? "", s.TotalFrames?.ToString() ?? "",
                Fmt(s.CpuPercent, 1), s.PssKb?.ToString() ?? "", s.RssKb?.ToString() ?? "",
                s.ThermalStatus?.ToString() ?? "", s.BatteryLevel?.ToString() ?? "", s.ThermalFresh, s.BatteryFresh, Fmt(s.SampleDurationMs, 1), ""));
        }
        while (markerIndex < orderedMarkers.Length) await WriteMarkerAsync(orderedMarkers[markerIndex++]);

        Task WriteMarkerAsync(ProfilerMarker marker) => writer.WriteLineAsync(
            marker.Timestamp.ToString("O") + ",,,,,,,,,,,,,," + CsvCell(marker.Label));
    }

    public static ProfilerSummary Summarize(IReadOnlyList<ProfilerSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return new ProfilerSummary();

        var fps = snapshots.Where(s => s.Fps.HasValue).Select(s => s.Fps!.Value).ToList();
        var cpu = snapshots.Where(s => s.CpuPercent.HasValue).Select(s => s.CpuPercent!.Value).ToList();
        var pss = snapshots.Where(s => s.PssKb.HasValue).Select(s => s.PssKb!.Value).ToList();
        var battery = snapshots.Where(s => s.BatteryLevel.HasValue).Select(s => s.BatteryLevel!.Value).ToList();
        var thermal = snapshots.Where(s => s.ThermalStatus.HasValue).Select(s => s.ThermalStatus!.Value).ToList();

        var minFps = fps.Count > 0 ? fps.Min() : (double?)null;
        return new ProfilerSummary
        {
            AvgFps = fps.Count > 0 ? fps.Average() : null,
            MinFps = minFps,
            FpsSampleCount = fps.Count,
            CpuSampleCount = cpu.Count,
            MemorySampleCount = pss.Count,
            JankyFrames = snapshots.Sum(s => s.JankyFrames ?? 0),
            MaxCpuPercent = cpu.Count > 0 ? cpu.Max() : null,
            MemoryGrowthKb = pss.Count > 1 ? pss[^1] - pss[0] : null,
            BatteryDrainPercent = battery.Count > 1 ? Math.Max(0, battery[0] - battery[^1]) : null,
            MaxThermalStatus = thermal.Count > 0 ? thermal.Max() : null,
            HasSufficientData = fps.Count >= 2,
            // §12.1 slow-session norms: sustained P90 frame time over 50 ms (20 FPS casual)
            SlowSession = snapshots.Zip(snapshots.Skip(1), (a, b) =>
                a.TotalFrames is > 0 && b.TotalFrames is > 0 &&
                a.FrameTimeP90Ms is > 50 && b.FrameTimeP90Ms is > 50).Any(x => x)
        };
    }

    private static string Fmt(double? v, int digits) => v.HasValue ? Math.Round(v.Value, digits).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    private static string CsvCell(string value) => "\"" + (value.Length > 0 && "=+-@".Contains(value[0]) ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
}

public sealed class ProfilerSummary
{
    public double? AvgFps { get; init; }
    public double? MinFps { get; init; }
    public int FpsSampleCount { get; init; }
    public int CpuSampleCount { get; init; }
    public int MemorySampleCount { get; init; }
    public int JankyFrames { get; init; }
    public double? MaxCpuPercent { get; init; }
    public int? MemoryGrowthKb { get; init; }
    public int? BatteryDrainPercent { get; init; }
    public int? MaxThermalStatus { get; init; }
    public bool HasSufficientData { get; init; }
    public bool SlowSession { get; init; }
    public string Verdict => !HasSufficientData ? "INSUFFICIENT DATA" : SlowSession ? "SLOW SESSION" : "OK";
}
