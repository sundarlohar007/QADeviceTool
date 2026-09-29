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
    private readonly object _lock = new();
    private readonly List<ProfilerSnapshot> _history = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _resolvedLayer;
    private bool _layerResolved;
    private long _lastPresentTimestampNs;
    private int _sampleNumber;
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
    }

    public event Action<ProfilerSnapshot>? SnapshotSampled;

    public IReadOnlyList<ProfilerSnapshot> History { get { lock (_lock) return _history.ToList(); } }

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
                        _history.Add(snapshot);
                        if (_history.Count > 7200) _history.RemoveAt(0); // ring-buffer cap (2h @1s)
                    }
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
        var fpsTask = SampleFpsAsync(cancellationToken);
        var cpu = await ProbeCpuAsync(cancellationToken).ConfigureAwait(false);
        var sample = Interlocked.Increment(ref _sampleNumber);
        // Memory is a soak-test correctness metric: sample every time so growth
        // between adjacent snapshots cannot be hidden by a cache interval.
        _lastMemory = await ProbeMemAsync(cancellationToken).ConfigureAwait(false);
        if (sample == 1 || sample % 10 == 0)
        {
            _lastThermal = await ProbeThermalAsync(cancellationToken).ConfigureAwait(false);
            _lastBattery = await ProbeBatteryAsync(cancellationToken).ConfigureAwait(false);
        }
        var (fps, p90, p95, janky, total) = await fpsTask.ConfigureAwait(false);

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
            BatteryLevel = _lastBattery
        };
    }

    private async Task<(double?, double?, double?, int?, int?)> SampleFpsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_layerResolved)
            {
                _resolvedLayer = _layerOverride ?? await ResolveLayerAsync(cancellationToken).ConfigureAwait(false);
                _layerResolved = _resolvedLayer != null;
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
            if (newFrames.Count == 0) return (null, null, null, 0, 0);
            var summary = AndroidDumpsysParsers.SummarizeFrames(newFrames, result.RefreshPeriodMs);
            var budget = result.RefreshPeriodMs * 1.05;
            return (summary.Fps, summary.FrameTimeP90Ms, summary.FrameTimeP95Ms,
                initial ? 0 : newFrames.Count(f => f.FrameTimeMs > budget), initial ? 0 : newFrames.Count);
        }
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
                var match = candidates.FirstOrDefault(x => x.Contains(_package, StringComparison.OrdinalIgnoreCase));
                return match;
            }
            foreach (var trimmed in candidates)
            {
                if (trimmed.StartsWith("SurfaceView", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("VRI[", StringComparison.Ordinal) ||
                    (string.IsNullOrEmpty(_package) && trimmed.Contains("BLAST", StringComparison.OrdinalIgnoreCase)))
                {
                    return trimmed;
                }
            }
            return null;
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Layer resolution failed"); return null; }
    }

    private static bool IsSafeLayer(string? layer) => !string.IsNullOrWhiteSpace(layer) &&
        layer.Length <= 512 && layer.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '/' or ':' or '[' or ']' or '(' or ')' or '#' or '@');

    private async Task<double?> ProbeCpuAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys cpuinfo", cancellationToken);
            return AndroidDumpsysParsers.ParseCpuPercent(output, _package);
        }
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
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Mem probe failed"); return (null, null); }
    }

    private async Task<int?> ProbeThermalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys thermalservice", cancellationToken);
            return AndroidDumpsysParsers.ParseThermalStatus(output);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Thermal probe failed"); return null; }
    }

    private async Task<int?> ProbeBatteryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys battery", cancellationToken);
            return AndroidDumpsysParsers.ParseBatteryLevel(output);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Battery probe failed"); return null; }
    }

    public void Dispose() => _ = StopAsync();
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
