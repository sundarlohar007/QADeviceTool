using LogPro.Services.Profiling;

namespace LogPro.Services.Profiling;

/// <summary>Result of a soak/endurance run (§12.5): duration-driven load + continuous sampling.</summary>
public sealed class SoakReport
{
    public TimeSpan Duration { get; init; }
    public int SampleCount { get; init; }
    public double? AvgFpsStart { get; init; }   // first third of the run
    public double? AvgFpsEnd { get; init; }     // last third of the run
    public double? FpsDecay { get; init; }      // AvgFpsStart - AvgFpsEnd (positive = decay)
    public int MemoryGrowthKb { get; init; }    // last PSS - first PSS
    public int JankyFrames { get; init; }
    public int MaxThermalStatus { get; init; }
    public bool MemoryGrowthFlagged { get; init; }
    public bool FpsDecayFlagged { get; init; }
    public bool ThermalFlagged { get; init; }
    public bool HasIssues => MemoryGrowthFlagged || FpsDecayFlagged || ThermalFlagged;
}

/// <summary>
/// Soak/endurance runner — replays load for a fixed duration while the profiler samples,
/// then flags memory growth, FPS decay and thermal throttle (§12.5).
/// </summary>
public static class SoakRunner
{
    private const double MemoryGrowthFlagKb = 150 * 1024;  // 150 MB growth over a run
    private const double FpsDecayFlag = 10.0;              // 10 FPS drop between start and end thirds
    private const int ThermalFlag = 1;                     // THROTTLING or worse

    public static async Task<SoakReport> RunAsync(
        IAdbService adb, string serial, string package,
        TimeSpan duration, Func<CancellationToken, Task> loadLoop, int sampleIntervalMs = 1000)
    {
        using var profiler = new AndroidPerformanceProfiler(adb, serial,
            string.IsNullOrWhiteSpace(package) ? null : package, intervalMs: sampleIntervalMs);
        profiler.Start();

        using var cts = new CancellationTokenSource(duration);
        var loadTask = Task.Run(async () =>
        {
            try { await loadLoop(cts.Token); }
            catch (OperationCanceledException) { /* run window elapsed */ }
        });

        await Task.Delay(duration);
        cts.Cancel();
        try { await loadTask; } catch (Exception ex) { AppLogger.Log.Debug(ex, "[Soak] Load loop faulted"); }
        await profiler.StopAsync();

        var history = profiler.History;
        if (history.Count == 0)
            return new SoakReport { Duration = duration };

        double? AverageFps(IEnumerable<ProfilerSnapshot> s)
        {
            var values = s.Where(x => x.Fps.HasValue).Select(x => x.Fps!.Value).ToList();
            return values.Count > 0 ? values.Average() : null;
        }

        var third = Math.Max(1, history.Count / 3);
        var firstThird = history.Take(third);
        var lastThird = history.Skip(history.Count - third);
        var avgStart = AverageFps(firstThird);
        var avgEnd = AverageFps(lastThird);

        var pss = history.Where(s => s.PssKb.HasValue).Select(s => s.PssKb!.Value).ToList();
        var memoryGrowth = pss.Count > 1 ? pss[^1] - pss[0] : 0;
        var thermalMax = history.Where(s => s.ThermalStatus.HasValue).Select(s => s.ThermalStatus!.Value).DefaultIfEmpty(0).Max();

        var fpsDecay = avgStart.HasValue && avgEnd.HasValue ? avgStart - avgEnd : null;

        return new SoakReport
        {
            Duration = duration,
            SampleCount = history.Count,
            AvgFpsStart = avgStart,
            AvgFpsEnd = avgEnd,
            FpsDecay = fpsDecay,
            MemoryGrowthKb = memoryGrowth,
            JankyFrames = history.Sum(s => s.JankyFrames ?? 0),
            MaxThermalStatus = thermalMax,
            MemoryGrowthFlagged = memoryGrowth > MemoryGrowthFlagKb,
            FpsDecayFlagged = fpsDecay is > FpsDecayFlag,
            ThermalFlagged = thermalMax >= ThermalFlag
        };
    }
}
