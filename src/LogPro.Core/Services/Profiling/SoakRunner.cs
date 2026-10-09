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
    public string? LoadError { get; init; }
    public bool LoadCompletedEarly { get; init; }
    public bool HasSufficientData { get; init; }
    public bool HasIssues => !HasSufficientData || LoadError != null || LoadCompletedEarly || MemoryGrowthFlagged || FpsDecayFlagged || ThermalFlagged;
}

/// <summary>
/// Soak/endurance runner — replays load for a fixed duration while the profiler samples,
/// then flags memory growth, FPS decay and thermal throttle (§12.5).
/// </summary>
public static class SoakRunner
{
    private const double MemoryGrowthFlagKb = 150 * 1024;  // 150 MB growth over a run
    private const double FpsDecayFlag = 10.0;              // 10 FPS drop between start and end thirds
    private const int ThermalFlag = 3;                     // Android THERMAL_STATUS_SEVERE or worse

    public static async Task<SoakReport> RunAsync(
        IAdbService adb, string serial, string package,
        TimeSpan duration, Func<CancellationToken, Task> loadLoop, int sampleIntervalMs = 1000,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        using var profiler = new AndroidPerformanceProfiler(adb, serial,
            string.IsNullOrWhiteSpace(package) ? null : package, intervalMs: sampleIntervalMs);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        double startSum = 0, endSum = 0;
        int startCount = 0, endCount = 0;
        profiler.SnapshotSampled += sample =>
        {
            if (sample.Fps is not { } fps) return;
            if (elapsed.Elapsed.TotalMilliseconds <= duration.TotalMilliseconds / 3) { startSum += fps; startCount++; }
            if (elapsed.Elapsed.TotalMilliseconds >= duration.TotalMilliseconds * 2 / 3) { endSum += fps; endCount++; }
        };
        profiler.Start();

        using var durationCts = new CancellationTokenSource(duration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, durationCts.Token);
        var loadTask = Task.Run(() => loadLoop(cts.Token));
        string? loadError = null;
        var loadCompletedEarly = false;

        try
        {
            var timeout = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            var completed = await Task.WhenAny(timeout, loadTask);
            if (completed == loadTask && !cts.IsCancellationRequested)
            {
                loadCompletedEarly = loadTask.IsCompletedSuccessfully;
                if (loadTask.IsCanceled) loadError = "Load cancelled before the soak duration ended.";
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            cts.Cancel();
            try { await loadTask; }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex) { loadError = LogPro.Helpers.SecurityHelper.RedactSensitiveText(ex.Message); AppLogger.Log.Debug(ex, "[Soak] Load loop faulted"); }
            await profiler.StopAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var history = profiler.History;
        if (history.Count == 0)
            return new SoakReport { Duration = duration, LoadError = loadError, LoadCompletedEarly = loadCompletedEarly };

        var summary = profiler.Summary;
        double? avgStart = startCount > 0 ? startSum / startCount : null;
        double? avgEnd = endCount > 0 ? endSum / endCount : null;
        var memoryGrowth = summary.MemoryGrowthKb ?? 0;
        var thermalMax = summary.MaxThermalStatus ?? 0;

        var fpsDecay = avgStart.HasValue && avgEnd.HasValue ? avgStart - avgEnd : null;

        return new SoakReport
        {
            Duration = duration,
            SampleCount = profiler.TotalSampleCount,
            AvgFpsStart = avgStart,
            AvgFpsEnd = avgEnd,
            FpsDecay = fpsDecay,
            MemoryGrowthKb = memoryGrowth,
            JankyFrames = summary.JankyFrames,
            MaxThermalStatus = thermalMax,
            MemoryGrowthFlagged = memoryGrowth > MemoryGrowthFlagKb,
            FpsDecayFlagged = fpsDecay is > FpsDecayFlag,
            ThermalFlagged = thermalMax >= ThermalFlag,
            LoadError = loadError,
            LoadCompletedEarly = loadCompletedEarly,
            HasSufficientData = summary.FpsSampleCount >= 2 && summary.MemorySampleCount >= 2 && avgStart.HasValue && avgEnd.HasValue
        };
    }
}
