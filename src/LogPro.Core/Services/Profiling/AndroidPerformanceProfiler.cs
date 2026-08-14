using LogPro.Models;

namespace LogPro.Services.Profiling;

/// <summary>
/// Background performance sampler (§12.1). Engine-agnostic: reads OS surfaces only
/// (SurfaceFlinger, cpuinfo, meminfo, thermalservice, battery) — never on the UI thread,
/// never in the caller's path. Emits <see cref="ProfilerSnapshot"/> per sample interval.
/// </summary>
public sealed class AndroidPerformanceProfiler : IDisposable
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

    public AndroidPerformanceProfiler(IAdbService adb, string serial, string? package = null,
        string? layerOverride = null, int intervalMs = 1000)
    {
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
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await SampleOnceAsync().ConfigureAwait(false);
                    lock (_lock)
                    {
                        _history.Add(snapshot);
                        if (_history.Count > 7200) _history.RemoveAt(0); // ring-buffer cap (2h @1s)
                    }
                    SnapshotSampled?.Invoke(snapshot);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Sample failed"); }
                try { await Task.Delay(_intervalMs, token).ConfigureAwait(false); }
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

    public async Task<ProfilerSnapshot> SampleOnceAsync()
    {
        var fpsTask = SampleFpsAsync();
        var cpu = await ProbeCpuAsync().ConfigureAwait(false);
        var mem = await ProbeMemAsync().ConfigureAwait(false);
        var thermal = await ProbeThermalAsync().ConfigureAwait(false);
        var battery = await ProbeBatteryAsync().ConfigureAwait(false);
        var (fps, p90, p95, janky, total) = await fpsTask.ConfigureAwait(false);

        return new ProfilerSnapshot
        {
            Fps = fps,
            FrameTimeP90Ms = p90,
            FrameTimeP95Ms = p95,
            JankyFrames = janky,
            TotalFrames = total,
            CpuPercent = cpu,
            PssKb = mem.PssKb,
            RssKb = mem.RssKb,
            ThermalStatus = thermal,
            BatteryLevel = battery
        };
    }

    private async Task<(double?, double?, double?, int?, int?)> SampleFpsAsync()
    {
        try
        {
            if (!_layerResolved)
            {
                _resolvedLayer = _layerOverride ?? await ResolveLayerAsync().ConfigureAwait(false);
                _layerResolved = true;
            }
            if (_resolvedLayer == null) return (null, null, null, null, null);

            var output = await _adb.ExecuteCommandAsync(_serial, $"shell dumpsys SurfaceFlinger --latency {_resolvedLayer}");
            var result = AndroidDumpsysParsers.ParseSurfaceFlingerLatency(output);
            return AndroidDumpsysParsers.SummarizeFrames(result.Frames, result.RefreshPeriodMs);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] FPS sample failed"); return (null, null, null, null, null); }
    }

    private async Task<string?> ResolveLayerAsync()
    {
        try
        {
            var listing = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys SurfaceFlinger --list");
            var lines = listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("SurfaceView", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("VRI[", StringComparison.Ordinal) ||
                    (string.IsNullOrEmpty(_package) && trimmed.Contains("BLAST", StringComparison.OrdinalIgnoreCase)))
                {
                    var name = trimmed.Split('[', ']')[0];
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
                if (_package != null && trimmed.Contains(_package, StringComparison.OrdinalIgnoreCase))
                    return trimmed.Split('[', ']')[0];
            }
            return null;
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Layer resolution failed"); return null; }
    }

    private async Task<double?> ProbeCpuAsync()
    {
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys cpuinfo");
            return AndroidDumpsysParsers.ParseCpuPercent(output, _package);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] CPU probe failed"); return null; }
    }

    private async Task<(int? PssKb, int? RssKb)> ProbeMemAsync()
    {
        if (string.IsNullOrWhiteSpace(_package)) return (null, null);
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, $"shell dumpsys meminfo {_package}");
            return AndroidDumpsysParsers.ParseMemInfoTotals(output);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Mem probe failed"); return (null, null); }
    }

    private async Task<int?> ProbeThermalAsync()
    {
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys thermalservice");
            return AndroidDumpsysParsers.ParseThermalStatus(output);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Thermal probe failed"); return null; }
    }

    private async Task<int?> ProbeBatteryAsync()
    {
        try
        {
            var output = await _adb.ExecuteCommandAsync(_serial, "shell dumpsys battery");
            return AndroidDumpsysParsers.ParseBatteryLevel(output);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Profiler] Battery probe failed"); return null; }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
