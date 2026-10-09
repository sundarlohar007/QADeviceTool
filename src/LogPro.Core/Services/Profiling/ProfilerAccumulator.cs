namespace LogPro.Services.Profiling;

/// <summary>Constant-space statistics for the entire run, independent of retained chart samples.</summary>
public sealed class ProfilerAccumulator
{
    private double _fpsSum;
    private double? _minFps, _maxCpu;
    private int _fps, _cpu, _memory, _battery, _jank;
    private int? _firstPss, _lastPss, _firstBattery, _lastBattery, _maxThermal;
    private bool _previousSlow, _slow;
    public int Count { get; private set; }
    public void Add(ProfilerSnapshot sample)
    {
        Count++;
        if (sample.Fps is { } fps) { _fps++; _fpsSum += fps; _minFps = Math.Min(_minFps ?? fps, fps); }
        if (sample.CpuPercent is { } cpu) { _cpu++; _maxCpu = Math.Max(_maxCpu ?? cpu, cpu); }
        if (sample.PssKb is { } pss) { _memory++; _firstPss ??= pss; _lastPss = pss; }
        if (sample.BatteryLevel is { } battery) { _battery++; _firstBattery ??= battery; _lastBattery = battery; }
        if (sample.ThermalStatus is { } thermal) _maxThermal = Math.Max(_maxThermal ?? thermal, thermal);
        _jank += sample.JankyFrames ?? 0;
        var slow = sample.TotalFrames is > 0 && sample.FrameTimeP90Ms is > 50;
        _slow |= _previousSlow && slow;
        _previousSlow = slow;
    }
    public ProfilerSummary Summary => new()
    {
        AvgFps = _fps > 0 ? _fpsSum / _fps : null,
        MinFps = _minFps,
        MaxCpuPercent = _maxCpu,
        FpsSampleCount = _fps,
        CpuSampleCount = _cpu,
        MemorySampleCount = _memory,
        JankyFrames = _jank,
        MemoryGrowthKb = _memory > 1 ? _lastPss - _firstPss : null,
        BatteryDrainPercent = _battery > 1 ? Math.Max(0, _firstBattery!.Value - _lastBattery!.Value) : null,
        MaxThermalStatus = _maxThermal,
        HasSufficientData = _fps >= 2,
        SlowSession = _slow
    };
}
