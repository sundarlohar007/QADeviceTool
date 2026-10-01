namespace LogPro.Services.Profiling;

/// <summary>A timestamped event in a profiler run.</summary>
public sealed record ProfilerMarker(DateTime Timestamp, string Label);

/// <summary>One sampling instant of device performance (§12.1 metrics).</summary>
public sealed class ProfilerSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public double? Fps { get; init; }
    public double? FrameTimeP90Ms { get; init; }
    public double? FrameTimeP95Ms { get; init; }
    public int? JankyFrames { get; init; }          // estimated presentation gaps > 2 refresh periods
    public int? TotalFrames { get; init; }
    public double? CpuPercent { get; init; }
    public int? PssKb { get; init; }
    public int? RssKb { get; init; }
    public int? ThermalStatus { get; init; }        // Android: 0 none, 1 light, 2 moderate, 3 severe, 4 critical, 5 emergency, 6 shutdown
    public int? BatteryLevel { get; init; }
    public bool ThermalFresh { get; init; }
    public bool BatteryFresh { get; init; }
    public double SampleDurationMs { get; init; }
}

/// <summary>A single present-timestamp pair decoded from SurfaceFlinger --latency.</summary>
public sealed record FrameSample(long PresentTimestampNs, double FrameTimeMs);

/// <summary>Decoded SurfaceFlinger latency stream.</summary>
public sealed class SurfaceFlingerLatencyResult
{
    public double RefreshPeriodMs { get; init; }
    public IReadOnlyList<FrameSample> Frames { get; init; } = Array.Empty<FrameSample>();
}
