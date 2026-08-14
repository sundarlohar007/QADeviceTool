namespace LogPro.Services.Profiling;

/// <summary>One sampling instant of device performance (§12.1 metrics).</summary>
public sealed class ProfilerSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public double? Fps { get; init; }
    public double? FrameTimeP90Ms { get; init; }
    public double? FrameTimeP95Ms { get; init; }
    public int? JankyFrames { get; init; }          // frames over the 60Hz budget in the last window
    public int? TotalFrames { get; init; }
    public double? CpuPercent { get; init; }
    public int? PssKb { get; init; }
    public int? RssKb { get; init; }
    public int? ThermalStatus { get; init; }        // 0=OK 1=THROTTLING 2=EMERGENCY 3=SHUTDOWN
    public int? BatteryLevel { get; init; }
}

/// <summary>A single present-timestamp pair decoded from SurfaceFlinger --latency.</summary>
public sealed record FrameSample(long PresentTimestampNs, double FrameTimeMs);

/// <summary>Decoded SurfaceFlinger latency stream.</summary>
public sealed class SurfaceFlingerLatencyResult
{
    public double RefreshPeriodMs { get; init; }
    public IReadOnlyList<FrameSample> Frames { get; init; } = Array.Empty<FrameSample>();
}
