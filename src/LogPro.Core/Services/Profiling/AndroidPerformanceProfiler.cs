using LogPro.Models;

namespace LogPro.Services.Profiling;

/// <summary>
/// Background performance sampler (§12.1). Engine-agnostic: reads OS surfaces only
/// (SurfaceFlinger, cpuinfo, meminfo, thermalservice, battery) — never on the UI thread,
/// never in the caller's path. Emits <see cref="ProfilerSnapshot"/> per sample interval.
/// </summary>
public sealed class AndroidPerformanceProfiler : IDisposable, IAsyncDisposable
{
    private readonly IAdbService _adb;
    private readonly string _serial;
    private readonly string? _package;
    private readonly string? _layerOverride;
    private readonly int _intervalMs;
    private readonly int _historyLimit;
    private readonly object _lock = new();
    private readonly List<ProfilerSnapshot> _history = new();
    private readonly ProfilerAccumulator _statistics = new();
    public ProfilerSummary Summary { get { lock (_lock) return _statistics.Summary; } }
    public int TotalSampleCount { get { lock (_lock) return _statistics.Count; } }
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _resolvedLayer;
    private bool _layerResolved;
    private int _nextLayerResolutionSample;
    private long _lastPresentTimestampNs;
    private int _sampleNumber;
    private int _totalSamples;
    private (int? PssKb, int? RssKb) _lastMemory;
    private int? _lastThermal;
    private int? _lastBattery;

    public AndroidPerformanceProfiler(IAdbService adb, string serial, string? package = null,
        string? layerOverride = null, int intervalMs = 1000)
    {
        if (!LogPro.Helpers.SecurityHelper.IsValidOfflineDeviceSelector(serial))
            throw new ArgumentException("Invalid device selector.", nameof(serial));
        if (package != null && !LogPro.Helpers.SecurityHelper.IsValidPackageName(package))
            throw new ArgumentException("Invalid package name.", nameof(package));
        if (layerOverride != null && !IsSafeLayer(layerOverride))
            throw new ArgumentException("Invalid SurfaceFlinger layer.", nameof(layerOverride));
        if (intervalMs < 1) throw new ArgumentOutOfRangeException(nameof(intervalMs));

        _adb = adb;
        _serial = serial;
        _package = package;
        _layerOverride = layerOverride;
        _intervalMs = intervalMs;
        _historyLimit = Math.Clamp(7_200_000 / intervalMs, 7200, 28800); // two hours at UI-supported cadences
    }

    public event Action<ProfilerSnapshot>? SnapshotSampled;

    public IReadOnlyList<ProfilerSnapshot> History { get { lock (_lock) return _history.ToList(); } }
    public int TotalSamples => Volatile.Read(ref _totalSamples);

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMs));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await SampleOnceAsync(token).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _statistics.Add(snapshot);
                        _history.Add(snapshot);
                        if (_history.Count > _historyLimit) _history.RemoveAt(0);
                    }
                    Interlocked.Increment(ref _totalSamples);
                    SnapshotSampled?.Invoke(snapshot);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Sample failed"); }
                try { if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) break; }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async Task<ProfilerSnapshot> SampleOnceAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var fpsTask = SampleFpsAsync(cancellationToken);
        var cpuTask = ProbeCpuAsync(cancellationToken);
        var sample = Interlocked.Increment(ref _sampleNumber);
        var memoryTask = ProbeMemAsync(cancellationToken);
        Task<int?>? thermalTask = null, batteryTask = null;
        if (sample == 1 || sample % 10 == 0)
        {
            thermalTask = ProbeThermalAsync(cancellationToken);
            batteryTask = ProbeBatteryAsync(cancellationToken);
        }
        var probes = new List<Task> { fpsTask, cpuTask, memoryTask };
        if (thermalTask != null) probes.Add(thermalTask);
        if (batteryTask != null) probes.Add(batteryTask);
        await Task.WhenAll(probes).ConfigureAwait(false);
        var (fps, p90, p95, janky, total) = await fpsTask.ConfigureAwait(false);
        var cpu = await cpuTask.ConfigureAwait(false);
        _lastMemory = await memoryTask.ConfigureAwait(false);
        if (thermalTask != null) _lastThermal = await thermalTask.ConfigureAwait(false);
        if (batteryTask != null) _lastBattery = await batteryTask.ConfigureAwait(false);

        return new ProfilerSnapshot
        {
            Fps = fps,
            FrameTimeP90Ms = p90,
            FrameTimeP95Ms = p95,
            JankyFrames = janky,
            TotalFrames = total,
            CpuPercent = cpu,
            PssKb = _lastMemory.PssKb,
            RssKb = _lastMemory.RssKb,
            ThermalStatus = _lastThermal,
            BatteryLevel = _lastBattery,
            ThermalFresh = thermalTask != null,
            BatteryFresh = batteryTask != null,
            SampleDurationMs = stopwatch.Elapsed.TotalMilliseconds
        };
    }

    private async Task<(double?, double?, double?, int?, int?)> SampleFpsAsync(CancellationToken cancellationToken)
    {
        if (_package == null && _layerOverride == null) return (null, null, null, null, null);
        try
        {
            if (!_layerResolved)
            {
                if (_sampleNumber < _nextLayerResolutionSample) return (null, null, null, null, null);
                _resolvedLayer = _layerOverride ?? await ResolveLayerAsync(cancellationToken).ConfigureAwait(false);
                _layerResolved = _resolvedLayer != null;
                if (!_layerResolved) _nextLayerResolutionSample = _sampleNumber + 5;
            }
            if (_resolvedLayer == null) return (null, null, null, null, null);

            var output = await _adb.ExecuteCommandAsync(_serial, $"shell dumpsys SurfaceFlinger --latency \"{_resolvedLayer}\"", cancellationToken);
            var result = AndroidDumpsysParsers.ParseSurfaceFlingerLatency(output);
            if (result.Frames.Count == 0)
            {
                if (_layerOverride == null) _layerResolved = false;
                return (null, null, null, null, null);
            }
            var newest = result.Frames[^1].PresentTimestampNs;
            if (newest < _lastPresentTimestampNs) _lastPresentTimestampNs = 0;
            var initial = _lastPresentTimestampNs == 0;
            var newFrames = result.Frames.Where(f => f.PresentTimestampNs > _lastPresentTimestampNs).ToList();
            _lastPresentTimestampNs = newest;
            if (initial) return (null, null, null, 0, 0); // establish a live baseline
            if (newFrames.Count == 0) return (null, null, null, 0, 0);
            var summary = AndroidDumpsysParsers.SummarizeFrames(newFrames, result.RefreshPeriodMs);
            var budget = result.RefreshPeriodMs * 2.0;
            return (summary.Fps, summary.FrameTimeP90Ms, summary.FrameTimeP95Ms,
                newFrames.Count(f => f.FrameTimeMs > budget), newFrames.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] FPS sample failed"); return (null, null, null, null, null); }
    }

    private async Task<string?> ResolveLayerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var listing = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys SurfaceFlinger --list", cancellationToken);
            var lines = listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var candidates = lines.Select(x => x.Trim()).Where(IsSafeLayer).ToList();
            if (_package != null)
            {
                var match = candidates.FirstOrDefault(x =>
                {
                    var index = x.IndexOf(_package, StringComparison.OrdinalIgnoreCase);
                    while (index >= 0)
                    {
                        var before = index == 0 || !IsPackageCharacter(x[index - 1]);
                        var end = index + _package.Length;
                        var after = end == x.Length || !IsPackageCharacter(x[end]);
                        if (before && after) return true;
                        index = x.IndexOf(_package, index + 1, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                });
                return match;
            }
            // Without an app or explicit layer, SurfaceFlinger has no safe target.
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Layer resolution failed"); return null; }
    }

    private static bool IsSafeLayer(string? layer) => !string.IsNullOrWhiteSpace(layer) &&
        layer.Length <= 512 && layer.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '/' or ':' or '[' or ']' or '(' or ')' or '#' or '@');

    private static bool IsPackageCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_';

    private async Task<double?> ProbeCpuAsync(CancellationToken cancellationToken)
    {
        if (_package == null) return null;
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys cpuinfo", cancellationToken);
            return AndroidDumpsysParsers.ParseCpuPercent(output, _package);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] CPU probe failed"); return null; }
    }

    private async Task<(int? PssKb, int? RssKb)> ProbeMemAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_package)) return (null, null);
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, $"shell dumpsys meminfo {_package}", cancellationToken);
            return AndroidDumpsysParsers.ParseMemInfoTotals(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Mem probe failed"); return (null, null); }
    }

    private async Task<int?> ProbeThermalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys thermalservice", cancellationToken);
            return AndroidDumpsysParsers.ParseThermalStatus(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Thermal probe failed"); return null; }
    }

    private async Task<int?> ProbeBatteryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys battery", cancellationToken);
            return AndroidDumpsysParsers.ParseBatteryLevel(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Battery probe failed"); return null; }
    }

    public void Dispose() => _ = StopAsync();
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
