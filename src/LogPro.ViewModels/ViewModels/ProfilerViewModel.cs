using System.Collections.ObjectModel;
using LogPro.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;
using LogPro.Services.Profiling;

namespace LogPro.ViewModels;

/// <summary>
/// Live performance profiler (§12.1) — drives the engine-agnostic sampler and exposes
/// the latest snapshot + a bounded history for charts. Shared by both front-ends.
/// </summary>
public partial class ProfilerViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adb;
    private readonly IDeviceStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly string? _packageOverride;
    private AndroidPerformanceProfiler? _profiler;
    private AndroidPerformanceProfiler? _stoppingProfiler;
    private Action<ProfilerSnapshot>? _sampleHandler;
    private int _generation;
    private string? _activeSerial;
    private string? _activePackage;
    private string? _listedSerial;
    private bool _disposed;
    private ProfilerSummary? _baseline;
    private bool _baselinePinned;
    private IReadOnlyList<ProfilerSnapshot> _completedSamples = Array.Empty<ProfilerSnapshot>();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private CancellationTokenSource? _comparisonCts;

    public ProfilerViewModel(IAdbService adb, IDeviceStore store, IUiDispatcher dispatcher, string? packageOverride = null)
    {
        _adb = adb;
        _store = store;
        _dispatcher = dispatcher;
        _packageOverride = packageOverride;
        TargetPackage = packageOverride ?? PreferencesService.Current.TargetPackageName ?? string.Empty;
        _store.Changed += OnDevicesChanged;
        OnDevicesChanged();
    }

    public ObservableCollection<ProfilerSnapshot> History { get; } = new();
    public ObservableCollection<DeviceInfo> AvailableDevices { get; } = new();
    public ObservableCollection<string> AvailablePackages { get; } = new();
    public ObservableCollection<ProfilerMarker> Markers { get; } = new();
    public ObservableCollection<string> RecentRuns { get; } = new();
    public ObservableCollection<TierResult> TierResults { get; } = new();

    [ObservableProperty] private bool _isProfiling;
    [ObservableProperty] private string _statusMessage = "Select a device and start profiling.";
    [ObservableProperty] private double? _fps;
    [ObservableProperty] private double? _frameP90Ms;
    [ObservableProperty] private double? _cpuPercent;
    [ObservableProperty] private int? _pssKb;
    [ObservableProperty] private int? _thermalStatus;
    [ObservableProperty] private int? _batteryLevel;
    [ObservableProperty] private int _jankyFrames;
    [ObservableProperty] private string _jankDisplay = "n/a";
    [ObservableProperty] private string _targetPackage = string.Empty;
    [ObservableProperty] private int _sampleIntervalMs = 1000;
    [ObservableProperty] private int _comparisonSeconds = 10;
    [ObservableProperty] private double _minimumAverageFps = 30;
    [ObservableProperty] private int _maximumMemoryGrowthMb = 150;
    [ObservableProperty] private double _allowedFpsRegressionPercent = 10;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _activeTarget = "No active run";
    [ObservableProperty] private string _metricAvailability = "No samples yet";
    [ObservableProperty] private string _sessionVerdict = "No run yet";
    [ObservableProperty] private string _baselineComparison = "No baseline yet";
    [ObservableProperty] private string _thermalAlert = string.Empty;
    [ObservableProperty] private string _performanceAlerts = string.Empty;
    [ObservableProperty] private string _memoryTrend = "Memory trend unavailable";
    [ObservableProperty] private string _sampleOverhead = string.Empty;
    [ObservableProperty] private IReadOnlyList<ProfilerSnapshot> _chartSamples = Array.Empty<ProfilerSnapshot>();
    [ObservableProperty] private string _fpsChartRange = "unavailable";
    [ObservableProperty] private string _cpuChartRange = "unavailable";
    [ObservableProperty] private string _memoryChartRange = "unavailable";

    private bool _reconcilingDevices;
    private int? _initialPss;
    private ProfilerAccumulator _runStatistics = new();
    private ProfilerSummary? _completedSummary;
    private int _completedSampleCount;

    public DeviceInfo? SelectedDevice
    {
        get => _store.SelectedDevice;
        set { if (_reconcilingDevices) return; if (value != null && !_store.Devices.Any(d => d.Serial == value.Serial && d.Platform == value.Platform)) return; _store.SelectedDevice = value; }
    }

    private void OnDevicesChanged()
    {
        if (_disposed) return;
        _reconcilingDevices = true;
        try
        {
            AvailableDevices.Clear();
            foreach (var device in _store.Devices.Where(d => d.Platform == DevicePlatform.Android)) AvailableDevices.Add(device);
        }
        finally { _reconcilingDevices = false; }
        OnPropertyChanged(nameof(SelectedDevice));
        if (_listedSerial != SelectedDevice?.Serial)
        {
            AvailablePackages.Clear();
            _listedSerial = SelectedDevice?.Serial;
        }
        if (_activeSerial == null) return;
        var current = _store.Devices.FirstOrDefault(d => d.Platform == DevicePlatform.Android && d.Serial == _activeSerial);
        if (IsUsable(current) && SelectedDevice?.Serial == _activeSerial) return;
        _ = StopAfterDeviceChangeAsync();
    }

    private async Task StopAfterDeviceChangeAsync()
    {
        try
        {
            await StopProfilingAsync();
            StatusMessage = "Profiling stopped: the target device changed or disconnected.";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Performance] Disconnect stop failed"); StatusMessage = "Could not stop profiling cleanly after device change."; }
    }

    [RelayCommand]
    public async Task RefreshPackagesAsync()
    {
        var device = SelectedDevice;
        if (!IsUsable(device)) { StatusMessage = "Select an online Android device to list apps."; return; }
        IsBusy = true;
        try
        {
            var result = await _adb.GetAppInventoryAsync(device!.Serial);
            if (device.Serial != SelectedDevice?.Serial) return;
            if (!result.Success) { StatusMessage = $"App list unavailable: {SecurityHelper.RedactSensitiveText(result.Error)}"; return; }
            AvailablePackages.Clear();
            foreach (var package in result.Apps.Select(a => a.PackageId).Where(SecurityHelper.IsValidPackageName).Distinct().Order()) AvailablePackages.Add(package);
            StatusMessage = $"Found {AvailablePackages.Count} apps. Select one for app FPS, CPU, and memory.";
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Performance] App inventory failed"); StatusMessage = "Could not load apps from this Android device."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public void StartProfiling()
    {
        if (_disposed || IsProfiling || IsBusy) return;
        var device = SelectedDevice;
        if (device == null)
        {
            StatusMessage = "Select a device first.";
            return;
        }
        if (device.Platform != DevicePlatform.Android)
        {
            StatusMessage = "Performance profiling is currently supported on Android only.";
            return;
        }
        if (!IsUsable(device)) { StatusMessage = "Device is offline, unauthorized, or reconnecting. Connect it before profiling."; return; }
        var package = (_packageOverride ?? TargetPackage ?? string.Empty).Trim();
        if (package.Length > 0 && !SecurityHelper.IsValidPackageName(package)) { StatusMessage = "Enter a valid Android package name."; return; }
        var packageNotListed = package.Length > 0 && AvailablePackages.Count > 0 && !AvailablePackages.Contains(package);
        if (SampleIntervalMs is < 250 or > 10000) { StatusMessage = "Sample interval must be 250–10000 ms."; return; }

        AndroidPerformanceProfiler profiler;
        try { profiler = new AndroidPerformanceProfiler(_adb, device.Serial, package.Length == 0 ? null : package, intervalMs: SampleIntervalMs); }
        catch (ArgumentException ex) { StatusMessage = ex.Message; return; }

        History.Clear();
        _initialPss = null;
        _runStatistics = new();
        _completedSummary = null;
        _completedSampleCount = 0;
        _completedSamples = Array.Empty<ProfilerSnapshot>();
        Markers.Clear();
        ChartSamples = Array.Empty<ProfilerSnapshot>();
        ResetMetrics();
        SessionVerdict = "Collecting data";
        BaselineComparison = _baseline == null ? "No baseline yet" : "Waiting for current run";
        _activeSerial = device.Serial;
        _activePackage = package;
        ActiveTarget = $"{device.DisplayName} · {(package.Length == 0 ? "device only" : package)} · {SampleIntervalMs} ms";
        var generation = Interlocked.Increment(ref _generation);
        Markers.Add(new ProfilerMarker(DateTime.UtcNow, "Run started"));
        _profiler = profiler;
        _sampleHandler = snapshot => OnSnapshot(snapshot, generation);
        profiler.SnapshotSampled += _sampleHandler;
        IsProfiling = true;
        profiler.Start();
        StatusMessage = packageNotListed
            ? "Selected app is not in the loaded inventory. Verify it is installed; app metrics may be unavailable."
            : package.Length == 0
            ? "Profiling device health. Select an app to enable app FPS, CPU, and memory."
            : $"Profiling {device.DisplayName} / {package}. First FPS poll establishes a baseline.";
    }

    [RelayCommand]
    public async Task StopProfilingAsync()
    {
        await _stopGate.WaitAsync();
        try
        {
            var profiler = _profiler;
            if (profiler == null) return;
            Interlocked.Increment(ref _generation);
            _profiler = null;
            _stoppingProfiler = profiler;
            if (_sampleHandler != null) profiler.SnapshotSampled -= _sampleHandler;
            _sampleHandler = null;
            await profiler.StopAsync();
            _completedSamples = profiler.History;
            _completedSummary = profiler.Summary;
            _completedSampleCount = profiler.TotalSampleCount;
            await profiler.DisposeAsync();
            _stoppingProfiler = null;
            Markers.Add(new ProfilerMarker(DateTime.UtcNow, "Run stopped"));
            IsProfiling = false;
            MetricAvailability = "Stopped · cards show the last captured values";
            _activeSerial = null;
            var summary = _completedSummary ?? ProfilerReportWriter.Summarize(_completedSamples);
            SessionVerdict = $"{summary.Verdict} · FPS {summary.FpsSampleCount}/{_completedSamples.Count}, CPU {summary.CpuSampleCount}/{_completedSamples.Count}, memory {summary.MemorySampleCount}/{_completedSamples.Count}";
            if (_baseline?.AvgFps is { } baseline && summary.AvgFps is { } current)
                BaselineComparison = $"Average FPS vs baseline: {current - baseline:+0.0;-0.0;0.0}";
            var alerts = new List<string>();
            if (!summary.HasSufficientData) alerts.Add("Insufficient live FPS data");
            var minimumFps = Math.Clamp(MinimumAverageFps, 1, 240);
            var maximumGrowthMb = Math.Clamp(MaximumMemoryGrowthMb, 1, 10000);
            var allowedRegression = Math.Clamp(AllowedFpsRegressionPercent, 1, 100);
            if (summary.AvgFps is { } avg && avg < minimumFps) alerts.Add($"Average FPS below {minimumFps:F0}");
            if (summary.MemoryGrowthKb is { } growth && growth > maximumGrowthMb * 1024L) alerts.Add($"Memory grew over {maximumGrowthMb} MB");
            if (_baseline?.AvgFps is { } prior && prior > 0 && summary.AvgFps is { } now &&
                (prior - now) / prior * 100 > allowedRegression)
                alerts.Add($"FPS regressed over {allowedRegression:F0}% vs baseline");
            PerformanceAlerts = alerts.Count == 0 ? "No threshold alerts" : string.Join(" · ", alerts);
            if (summary.HasSufficientData && !_baselinePinned) _baseline = summary;
            RecentRuns.Insert(0, $"{DateTime.Now:g} · {_activePackage} · {_completedSampleCount} samples · {summary.Verdict} · avg FPS {summary.AvgFps?.ToString("F1") ?? "n/a"}");
            while (RecentRuns.Count > 20) RecentRuns.RemoveAt(RecentRuns.Count - 1);
            StatusMessage = profiler.TotalSamples > _completedSamples.Count
                ? $"Stopped — showing the last {_completedSamples.Count} of {profiler.TotalSamples} samples (buffer limit); {summary.Verdict.ToLowerInvariant()}."
                : $"Stopped — {_completedSamples.Count} samples; {summary.Verdict.ToLowerInvariant()}.";
        }
        finally { _stopGate.Release(); }
    }

    [RelayCommand]
    public void AddMarker()
    {
        if (IsProfiling) Markers.Add(new ProfilerMarker(DateTime.UtcNow, "Manual marker"));
    }

    [RelayCommand]
    public void PinBaseline()
    {
        if (IsProfiling) { StatusMessage = "Stop the run before pinning it as a baseline."; return; }
        var summary = _completedSummary ?? (_runStatistics.Count > 0 ? _runStatistics.Summary : ProfilerReportWriter.Summarize(History.ToArray()));
        if (!summary.HasSufficientData) { StatusMessage = "Collect at least two live FPS samples before pinning a baseline."; return; }
        _baseline = summary;
        _baselinePinned = true;
        BaselineComparison = $"Pinned baseline: {summary.AvgFps:F1} average FPS.";
    }

    [RelayCommand]
    public void ClearBaseline()
    {
        _baseline = null;
        _baselinePinned = false;
        BaselineComparison = "No baseline yet";
    }

    [RelayCommand]
    public async Task ExportJsonAsync() => await ExportAsync(true);

    [RelayCommand]
    public async Task ExportCsvAsync() => await ExportAsync(false);

    private async Task ExportAsync(bool json)
    {
        var samples = (_profiler?.History ?? _stoppingProfiler?.History ?? (_completedSamples.Count > 0 ? _completedSamples : History.ToArray())).ToArray();
        if (samples.Length == 0) { StatusMessage = "No samples to export."; return; }
        var extension = json ? "json" : "csv";
        var path = await UiServices.Files.SaveFileAsync("Export Performance run", $"{extension.ToUpperInvariant()} (*.{extension})|*.{extension}", $"performance-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");
        if (path == null) return;
        if (!PathHelper.IsSafeLocalPath(path)) { StatusMessage = "Choose a safe local export path."; return; }
        try
        {
            if (json) await ProfilerReportWriter.WriteJsonAsync(samples, path, Markers.ToArray(), _profiler?.Summary ?? _stoppingProfiler?.Summary ?? _completedSummary ?? _runStatistics.Summary, _profiler?.TotalSampleCount ?? _stoppingProfiler?.TotalSampleCount ?? (_completedSampleCount > 0 ? _completedSampleCount : _runStatistics.Count));
            else await ProfilerReportWriter.WriteCsvAsync(samples, path, Markers.ToArray());
            StatusMessage = $"Exported {samples.Length} samples to {Path.GetFileName(path)}.";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Performance] Export failed"); StatusMessage = "Performance export failed. Check the selected path and retry."; }
    }

    [RelayCommand]
    public async Task CompareDevicesAsync()
    {
        if (IsProfiling || IsBusy) return;
        var devices = AvailableDevices.Where(IsUsable).ToArray();
        if (devices.Length < 2) { StatusMessage = "Connect at least two online Android devices for tier comparison."; return; }
        var package = (TargetPackage ?? string.Empty).Trim();
        if (!SecurityHelper.IsValidPackageName(package)) { StatusMessage = "Choose a valid app package for tier comparison."; return; }
        if (ComparisonSeconds is < 3 or > 120) { StatusMessage = "Comparison duration must be 3–120 seconds."; return; }
        if (SampleIntervalMs is < 250 or > 10000) { StatusMessage = "Sample interval must be 250–10000 ms."; return; }
        IsBusy = true;
        _comparisonCts = new CancellationTokenSource();
        StatusMessage = $"Comparing {devices.Length} devices for {ComparisonSeconds} seconds…";
        try
        {
            var profiles = devices.Select(d => new DeviceTierProfile { Serial = d.Serial, Label = d.DisplayName, OsVersion = d.OsVersion }).ToArray();
            var results = await TierMatrix.CompareAsync(_adb, profiles, package, TimeSpan.FromSeconds(ComparisonSeconds), SampleIntervalMs, _comparisonCts.Token);
            TierResults.Clear();
            foreach (var result in results) TierResults.Add(result);
            StatusMessage = $"Comparison complete for {TierResults.Count} devices. Missing metrics show as unavailable.";
        }
        catch (OperationCanceledException) { StatusMessage = "Tier comparison cancelled."; }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[Performance] Tier comparison failed"); StatusMessage = "Tier comparison failed. Check device connections and retry."; }
        finally { _comparisonCts?.Dispose(); _comparisonCts = null; IsBusy = false; }
    }

    [RelayCommand]
    public void CancelComparison() => _comparisonCts?.Cancel();

    private void OnSnapshot(ProfilerSnapshot snapshot, int generation)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed || generation != Volatile.Read(ref _generation) || !IsProfiling) return;
            Fps = snapshot.Fps;
            FrameP90Ms = snapshot.FrameTimeP90Ms;
            CpuPercent = snapshot.CpuPercent;
            PssKb = snapshot.PssKb;
            ThermalStatus = snapshot.ThermalStatus;
            BatteryLevel = snapshot.BatteryLevel;
            JankyFrames += snapshot.JankyFrames ?? 0;
            if (snapshot.JankyFrames.HasValue) JankDisplay = JankyFrames.ToString();
            MetricAvailability = $"FPS {Availability(snapshot.Fps)} · CPU {Availability(snapshot.CpuPercent)} · PSS {Availability(snapshot.PssKb)} · thermal {Freshness(snapshot.ThermalStatus, snapshot.ThermalFresh)} · battery {Freshness(snapshot.BatteryLevel, snapshot.BatteryFresh)}";
            ThermalAlert = snapshot.ThermalStatus is >= 3 ? "Thermal alert: severe or higher; performance may be throttled." : string.Empty;
            var firstPss = _initialPss ??= snapshot.PssKb;
            MemoryTrend = firstPss.HasValue && snapshot.PssKb.HasValue
                ? $"Memory trend: {(snapshot.PssKb.Value - firstPss.Value) / 1024.0:+0.0;-0.0;0.0} MB since start"
                : "Memory trend unavailable";
            SampleOverhead = $"Sample probe time {snapshot.SampleDurationMs:F0} ms / interval {SampleIntervalMs} ms" +
                (snapshot.SampleDurationMs > SampleIntervalMs ? " — sampling is falling behind." : string.Empty);
            if (!snapshot.TotalFrames.HasValue && _activePackage?.Length > 0)
                StatusMessage = "FPS unavailable: this app exposes no usable SurfaceFlinger layer. Other metrics continue.";
            else if (snapshot.TotalFrames == 0)
                StatusMessage = "Profiling — no new frames in the latest sample (or establishing baseline).";
            else
                StatusMessage = $"Profiling {ActiveTarget}.";

            History.Add(snapshot);
            if (History.Count > 600) History.RemoveAt(0);
            ChartSamples = History.TakeLast(120).ToArray();
            FpsChartRange = Range(ChartSamples.Select(s => s.Fps), "FPS");
            CpuChartRange = Range(ChartSamples.Select(s => s.CpuPercent), "%");
            MemoryChartRange = Range(ChartSamples.Select(s => s.PssKb.HasValue ? s.PssKb.Value / 1024.0 : (double?)null), "MB");
            _runStatistics.Add(snapshot);
            var quality = _runStatistics.Summary;
            SessionVerdict = $"{quality.Verdict} · FPS {quality.FpsSampleCount}/{_runStatistics.Count}, CPU {quality.CpuSampleCount}/{_runStatistics.Count}, memory {quality.MemorySampleCount}/{_runStatistics.Count}";
        });
    }

    private static string Availability<T>(T? value) where T : struct => value.HasValue ? "live" : "unavailable";
    private static string Freshness<T>(T? value, bool fresh) where T : struct => !value.HasValue ? "unavailable" : fresh ? "live" : "cached";
    private static string Range(IEnumerable<double?> values, string unit)
    {
        var tail = values.ToList();
        var lastGap = tail.FindLastIndex(v => !v.HasValue);
        var live = tail.Skip(lastGap + 1).Select(v => v!.Value).ToArray();
        return live.Length < 2 ? "unavailable" : $"{live.Min():F1}–{live.Max():F1} {unit}";
    }
    private static bool IsUsable(DeviceInfo? d) => d is { Platform: DevicePlatform.Android, ConnectionState: DeviceConnectionState.Online, IsTemporarilyUnavailable: false };

    private void ResetMetrics()
    {
        Fps = null; FrameP90Ms = null; CpuPercent = null; PssKb = null; ThermalStatus = null; BatteryLevel = null;
        JankyFrames = 0; JankDisplay = "n/a"; ThermalAlert = string.Empty; PerformanceAlerts = string.Empty;
        MemoryTrend = "Memory trend unavailable"; MetricAvailability = "Waiting for first sample";
        FpsChartRange = "unavailable"; CpuChartRange = "unavailable"; MemoryChartRange = "unavailable";
        SampleOverhead = $"Polling every {SampleIntervalMs} ms using adb probes.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Changed -= OnDevicesChanged;
        _comparisonCts?.Cancel();
        Interlocked.Increment(ref _generation);
        if (_profiler != null)
        {
            if (_sampleHandler != null) _profiler.SnapshotSampled -= _sampleHandler;
            _profiler.Dispose();
            _profiler = null;
        }
        GC.SuppressFinalize(this);
    }
}
