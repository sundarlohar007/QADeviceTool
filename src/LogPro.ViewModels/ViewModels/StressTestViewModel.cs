using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;
using LogPro.Services.Profiling;

namespace LogPro.ViewModels;

public partial class StressTestViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly List<MetricSnapshot> _metricSnapshots = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly IMonkeyProcessRunner _runner;

    private CancellationTokenSource? _runCts;
    private AndroidPerformanceProfiler? _profiler;
    private string? _runningOnSerial;
    private int _runActive;
    private int _disposed;
    private int _crashCountBacking;
    private int _anrCountBacking;
    private DateTime _runStartedAt;
    private List<AppItem> _allApps = new();
    private readonly ConcurrentQueue<string> _pendingOutput = new();
    private int _pendingOutputCount;
    private int _appRequestVersion;
    private bool _replacingDevices;
    private int _monkeyFinishedFlag;
    private int _runOffset;
    private int _eventsAttemptedBacking;
    private int _activeEventBudget;
    private string _activeRunMode = "Events";
    private int _activeDurationSeconds;
    private readonly object _outputLock = new();
    private StressRunSummary? _lastSummary;
    private string _lastRawOutput = string.Empty;
    private string? _lastRunSerial;
    private int _genericAbortFlag;

    [ObservableProperty] private ObservableCollection<DeviceInfo> _devices = new();
    [ObservableProperty] private DeviceInfo? _selectedDevice;

    [ObservableProperty] private ObservableCollection<AppItem> _filteredApps = new();
    [ObservableProperty] private AppItem? _selectedApp;
    [ObservableProperty] private string _appSearchQuery = string.Empty;
    [ObservableProperty] private bool _isLoadingApps;
    [ObservableProperty] private bool _isCheckingReadiness;
    [ObservableProperty] private string _readinessMessage = "Select an online Android device and target package.";

    [ObservableProperty] private string _targetPackage = string.Empty;
    [ObservableProperty] private int _eventCount = 1000;
    [ObservableProperty] private int _seed = 0;
    [ObservableProperty] private int _throttleMs = 300;
    [ObservableProperty] private int _pctTouch = 50; // defaults must sum to 100 (validation requires it)
    [ObservableProperty] private int _pctMotion = 20;
    [ObservableProperty] private int _pctTrackball = 5;
    [ObservableProperty] private int _pctNav = 10;
    [ObservableProperty] private int _pctSyskeys = 5;
    [ObservableProperty] private int _pctAppswitch = 10;
    [ObservableProperty] private bool _safeMode;
    [ObservableProperty] private bool _autoSaveRuns;
    [ObservableProperty] private bool _captureFailureScreenshot;
    [ObservableProperty] private string _runMode = "Events";
    [ObservableProperty] private int _durationSeconds = 600;
    public string[] RunModes { get; } = ["Events", "Duration"];
    public string[] Presets { get; } = ["Balanced", "Touch", "Navigation", "Safe"];
    public string EventGoalDisplay => RunMode == "Duration" ? "soak" : EventCount.ToString(CultureInfo.CurrentCulture);
    public string EstimatedDuration
    {
        get
        {
            if (RunMode == "Duration") return $"About {Math.Max(0, DurationSeconds) / 60}m {Math.Max(0, DurationSeconds) % 60}s";
            var milliseconds = (long)Math.Clamp(EventCount, 0, 1_000_000) * Math.Clamp(ThrottleMs, 0, 60_000);
            var estimate = TimeSpan.FromMilliseconds(milliseconds);
            return $"At least {(int)estimate.TotalHours:D2}:{estimate.Minutes:D2}:{estimate.Seconds:D2} plus injection time";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isRunning;

    public bool CanRun => IsPlatformSupported && !IsRunning;
    private readonly System.Text.StringBuilder _outputBuffer = new();
    [ObservableProperty] private string _output = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _crashCount;
    [ObservableProperty] private int _anrCount;
    [ObservableProperty] private int _eventsInjected;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _platformBadge = string.Empty;
    [ObservableProperty] private double? _latestFps;
    [ObservableProperty] private double? _latestCpuPercent;
    [ObservableProperty] private int? _latestPssKb;
    [ObservableProperty] private int? _latestThermalStatus;
    [ObservableProperty] private StressRunHistoryItem? _selectedBaselineRun;
    [ObservableProperty] private string _comparisonSummary = "Choose a prior run to compare results.";
    public ObservableCollection<StressRunHistoryItem> RunHistory { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isPlatformSupported;

    public StressTestViewModel(IAdbService adbService, IDeviceMonitorService deviceMonitor, IUiDispatcher? dispatcher = null,
        IMonkeyProcessRunner? runner = null)
    {
        _adbService = adbService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;
        _runner = runner ?? new MonkeyProcessRunner(adbService);

        TargetPackage = PreferencesService.Current.TargetPackageName;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        _deviceMonitor.DeviceDisconnected += OnDeviceDisconnected;
        OnDevicesChanged(_deviceMonitor.CurrentDevices.ToList());
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var previous = SelectedDevice;
            _replacingDevices = true;
            Devices.Clear();
            foreach (var d in devices) Devices.Add(d);
            SelectedDevice = previous == null
                ? Devices.FirstOrDefault(d => d.Platform == DevicePlatform.Android && d.ConnectionState == DeviceConnectionState.Online) ?? Devices.FirstOrDefault()
                : Devices.FirstOrDefault(d => d.Serial == previous.Serial && d.Platform == previous.Platform);
            _replacingDevices = false;
            if (previous?.Serial != SelectedDevice?.Serial)
            {
                _allApps.Clear();
                FilteredApps.Clear();
                SelectedApp = null;
                Interlocked.Increment(ref _appRequestVersion);
            }
            UpdateDeviceSupport();
            if (IsRunning && _runningOnSerial != null &&
                !Devices.Any(d => d.Serial == _runningOnSerial && d.Platform == DevicePlatform.Android &&
                    d.ConnectionState == DeviceConnectionState.Online && !d.IsTemporarilyUnavailable))
            {
                StatusMessage = "Device disconnected or unavailable; stopping stress test.";
                StopMonkey();
            }
        });
    }

    private void OnDeviceDisconnected(DeviceInfo device)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!IsRunning) return;
        if (_runningOnSerial != null && device.Serial == _runningOnSerial)
        {
            _dispatcher.Post(() =>
            {
                AppendOutput("\n[!] Device disconnected — stopping monkey.");
                StopMonkey();
            });
        }
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        var match = Devices.FirstOrDefault(d => d.Serial == device.Serial && d.Platform == device.Platform);
        if (match != null) SelectedDevice = match;
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (_replacingDevices) return;
        UpdateDeviceSupport();
        _allApps.Clear();
        FilteredApps.Clear();
        SelectedApp = null;
        Interlocked.Increment(ref _appRequestVersion);
        if (IsRunning && value?.Serial != _runningOnSerial) StopMonkey();
    }

    private void UpdateDeviceSupport()
    {
        var device = SelectedDevice;
        IsPlatformSupported = device?.Platform == DevicePlatform.Android &&
            device.ConnectionState == DeviceConnectionState.Online && !device.IsTemporarilyUnavailable;
        PlatformBadge = device?.Platform == DevicePlatform.iOS
            ? "iOS stress input is unsupported by pymobiledevice3."
            : device == null ? "Select an Android device."
            : !IsPlatformSupported ? "Android device is offline or reconnecting."
            : "Android Monkey is available for this device.";
        ReadinessMessage = PlatformBadge;
    }

    partial void OnSelectedAppChanged(AppItem? value)
    {
        if (value != null) TargetPackage = value.PackageId;
    }

    partial void OnTargetPackageChanged(string value) => ReadinessMessage = "Check the selected device and package before running.";

    partial void OnAppSearchQueryChanged(string value) => ApplyAppFilter();
    partial void OnEventCountChanged(int value)
    {
        OnPropertyChanged(nameof(EstimatedDuration));
        OnPropertyChanged(nameof(EventGoalDisplay));
    }
    partial void OnThrottleMsChanged(int value) => OnPropertyChanged(nameof(EstimatedDuration));
    partial void OnRunModeChanged(string value)
    {
        OnPropertyChanged(nameof(EstimatedDuration));
        OnPropertyChanged(nameof(EventGoalDisplay));
    }
    partial void OnDurationSecondsChanged(int value) => OnPropertyChanged(nameof(EstimatedDuration));
    partial void OnSafeModeChanged(bool value)
    {
        if (value) ApplyPreset("Safe");
    }

    [RelayCommand]
    private void ApplyPreset(string? preset)
    {
        switch (preset)
        {
            case "Balanced": (PctTouch, PctMotion, PctTrackball, PctNav, PctSyskeys, PctAppswitch) = (50, 20, 5, 10, 5, 10); SafeMode = false; break;
            case "Touch": (PctTouch, PctMotion, PctTrackball, PctNav, PctSyskeys, PctAppswitch) = (70, 25, 0, 5, 0, 0); SafeMode = false; break;
            case "Navigation": (PctTouch, PctMotion, PctTrackball, PctNav, PctSyskeys, PctAppswitch) = (20, 10, 0, 60, 0, 10); SafeMode = false; break;
            case "Safe": (PctTouch, PctMotion, PctTrackball, PctNav, PctSyskeys, PctAppswitch) = (70, 25, 0, 5, 0, 0); SafeMode = true; break;
        }
    }



    [RelayCommand]
    public async Task RefreshAppsAsync()
    {
        if (!IsPlatformSupported || SelectedDevice == null) return;
        var serial = SelectedDevice.Serial;
        var version = Interlocked.Increment(ref _appRequestVersion);
        IsLoadingApps = true;
        StatusMessage = "Loading installed apps...";
        try
        {
            var inventory = await _adbService.GetAppInventoryAsync(serial);
            if (version != _appRequestVersion || SelectedDevice?.Serial != serial || Volatile.Read(ref _disposed) != 0) return;
            if (!inventory.Success) throw new InvalidOperationException("Installed-app inventory could not be loaded.");
            _allApps = inventory.Apps.ToList();
            ApplyAppFilter();
            StatusMessage = $"Loaded {_allApps.Count} apps. Type to search.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[StressTest] RefreshAppsAsync failed");
            if (version == _appRequestVersion) StatusMessage = $"[!] Load apps failed: {ex.Message}";
        }
        finally { if (version == _appRequestVersion) IsLoadingApps = false; }
    }

    private void ApplyAppFilter()
    {
        FilteredApps.Clear();
        var q = (AppSearchQuery ?? "").Trim();
        IEnumerable<AppItem> matches = _allApps;
        if (!string.IsNullOrEmpty(q))
        {
            matches = _allApps.Where(a =>
                a.PackageId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (a.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        foreach (var a in matches.Take(50)) FilteredApps.Add(a);
    }

    [RelayCommand]
    private async Task CheckReadinessAsync()
    {
        if (IsCheckingReadiness) return;
        IsCheckingReadiness = true;
        try
        {
            var device = SelectedDevice;
            var package = TargetPackage?.Trim() ?? string.Empty;
            var problem = await CheckPreflightAsync(device, package);
            if (problem == null)
            {
                try
                {
                    if (RunMode is not ("Events" or "Duration") ||
                        (RunMode == "Duration" && DurationSeconds is < 30 or > 7200))
                        throw new ArgumentException("Choose a valid run mode and duration (30–7200 seconds).");
                    if (SafeMode && (PctSyskeys != 0 || PctAppswitch != 0))
                        throw new ArgumentException("Safe mode requires zero system-key and app-switch events.");
                    MonkeyProcessRunner.Validate(new MonkeyRunOptions(device!.Serial, package, EventCount, Seed,
                        ThrottleMs, PctTouch, PctMotion, PctTrackball, PctNav, PctSyskeys, PctAppswitch));
                }
                catch (ArgumentException ex) { problem = ex.Message; }
            }
            if (device?.Serial != SelectedDevice?.Serial || package != (TargetPackage?.Trim() ?? string.Empty))
                problem = "Inputs changed during preflight. Check readiness again.";
            ReadinessMessage = problem ?? $"Ready to exercise {package} on {device!.DisplayName}.";
            StatusMessage = problem == null ? "Stress test preflight passed." : "[!] " + problem;
        }
        catch (Exception ex) { ReadinessMessage = $"Preflight failed: {ex.Message}"; }
        finally { IsCheckingReadiness = false; }
    }

    private async Task<string?> CheckPreflightAsync(DeviceInfo? device, string package)
    {
        if (device == null) return "Select an Android device.";
        if (device.Platform != DevicePlatform.Android)
            return "iOS stress input is unsupported by pymobiledevice3.";
        if (device.ConnectionState != DeviceConnectionState.Online || device.IsTemporarilyUnavailable)
            return "Android device is offline or reconnecting.";
        if (!SecurityHelper.IsValidOfflineDeviceSelector(device.Serial))
            return "Device selector is not permitted by the offline policy.";
        if (!SecurityHelper.IsValidPackageName(package))
            return "Enter a valid Android package ID.";
        var result = await _adbService.ExecuteCommandWithResultAsync(device.Serial, $"shell pm path {package}");
        if (!result.Success || !result.Output.Contains("package:", StringComparison.Ordinal))
            return $"Package {package} is not installed or cannot be queried on this device.";
        var existing = await _adbService.ExecuteCommandWithResultAsync(device.Serial, "shell pidof com.android.commands.monkey");
        if (!existing.Success && !string.IsNullOrWhiteSpace(existing.Error))
            return "Could not verify whether another Monkey process is running on this device.";
        if (existing.Success && existing.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(value => int.TryParse(value, out var pid) && pid > 0))
            return "Another Monkey process is already running on this device.";
        return null;
    }

    [RelayCommand]
    private async Task RunMonkeyAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _runActive, 1) != 0) return;
        CancellationTokenSource? userCts = null;
        CancellationTokenSource? durationCts = null;
        CancellationTokenSource? linkedCts = null;
        AndroidPerformanceProfiler? profiler = null;
        try
        {
            var device = SelectedDevice;
            var package = TargetPackage?.Trim() ?? string.Empty;
            var mode = RunMode;
            var durationSeconds = DurationSeconds;
            var eventBudget = EventCount;
            var throttle = ThrottleMs;
            var seed = Seed;
            var mix = (PctTouch, PctMotion, PctTrackball, PctNav, PctSyskeys, PctAppswitch);
            if (device == null) throw new InvalidOperationException("No device selected. Select an Android device.");
            if (device.Platform != DevicePlatform.Android)
                throw new InvalidOperationException("Android-only stress input; iOS is unsupported by pymobiledevice3.");
            if (!SecurityHelper.IsValidPackageName(package))
                throw new InvalidOperationException("Invalid package name. Enter an Android package ID.");
            if (mode is not ("Events" or "Duration")) throw new InvalidOperationException("Unknown run mode.");
            if (eventBudget is < 1 or > 1_000_000)
                throw new InvalidOperationException("Event count must be between 1 and 1,000,000.");
            if (mode == "Duration" && durationSeconds is < 30 or > 7200)
                throw new InvalidOperationException("Duration must be between 30 seconds and 2 hours.");
            if (SafeMode && (mix.PctSyskeys != 0 || mix.PctAppswitch != 0))
                throw new InvalidOperationException("Safe mode requires system-key and app-switch percentages to be zero.");
            var options = new MonkeyRunOptions(device?.Serial ?? "", package,
                mode == "Events" ? eventBudget : Math.Clamp(eventBudget, 100, 100_000),
                seed, throttle, mix.PctTouch, mix.PctMotion, mix.PctTrackball,
                mix.PctNav, mix.PctSyskeys, mix.PctAppswitch);
            MonkeyProcessRunner.Validate(options);
            var problem = await CheckPreflightAsync(device, package);
            if (problem != null) throw new InvalidOperationException(problem);
            if (SelectedDevice?.Serial != device!.Serial)
                throw new InvalidOperationException("The selected device changed during preflight.");

            userCts = new CancellationTokenSource();
            _runCts = userCts;
            if (mode == "Duration") durationCts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
            linkedCts = durationCts == null ? null :
                CancellationTokenSource.CreateLinkedTokenSource(userCts.Token, durationCts.Token);
            var runToken = linkedCts?.Token ?? userCts.Token;
            var startedUtc = DateTime.UtcNow;
            var deviceName = SecurityHelper.RedactSensitiveText(device.DisplayName)
                .Replace(device.Serial, "[DEVICE]", StringComparison.Ordinal);
            var serialHash = SecurityHelper.HashSerial(device.Serial);
            _runningOnSerial = device.Serial;
            _runStartedAt = startedUtc;
            ResetRunState();
            _activeEventBudget = eventBudget;
            _activeRunMode = mode;
            _activeDurationSeconds = durationSeconds;
            IsRunning = true;
            StatusMessage = $"Running Monkey on {package} ({mode.ToLowerInvariant()} mode)…";
            AppendOutput($"[RUN] Device {serialHash}; package {package}; seed {seed}; mode {mode}.");

            profiler = new AndroidPerformanceProfiler(_adbService, device.Serial, package, intervalMs: 2000);
            _profiler = profiler;
            profiler.SnapshotSampled += OnProfilerSample;
            profiler.Start();

            var execution = new MonkeyExecutionResult(-1, false, false);
            var cycle = 0;
            var cyclesExecuted = 0;
            while (true)
            {
                if (durationCts?.IsCancellationRequested == true && cycle > 0) break;
                runToken.ThrowIfCancellationRequested();
                _runOffset = Volatile.Read(ref _eventsAttemptedBacking);
                Interlocked.Exchange(ref _monkeyFinishedFlag, 0);
                var cycleOptions = options with { Seed = unchecked(seed + cycle) };
                AppendOutput($"[CYCLE {cycle + 1}] seed {cycleOptions.Seed}; event budget {cycleOptions.EventCount}.");
                execution = await _runner.RunAsync(cycleOptions, HandleOutputLine, runToken);
                cyclesExecuted++;
                if (execution.Cancelled || execution.ExitCode != 0 ||
                    Volatile.Read(ref _monkeyFinishedFlag) == 0 || mode == "Events") break;
                cycle++;
                if (durationCts?.IsCancellationRequested == true) break;
            }

            await profiler.StopAsync();
            profiler.SnapshotSampled -= OnProfilerSample;
            _profiler = null;
            StressPerformanceMetrics metrics;
            try { metrics = await CollectPerformanceMetricsAsync(device.Serial, package); }
            catch (Exception ex)
            {
                AppLogger.Log.Debug(ex, "[StressTest] Final metrics unavailable");
                metrics = new StressPerformanceMetrics();
            }
            List<MetricSnapshot> snapshots;
            lock (_metricSnapshots) snapshots = _metricSnapshots.ToList();
            var attempted = Volatile.Read(ref _eventsAttemptedBacking);
            var durationExpired = durationCts?.IsCancellationRequested == true && !userCts.IsCancellationRequested;
            var outcome = execution.Cancelled
                ? durationExpired && execution.RemoteStopConfirmed ? "DurationComplete" : "Cancelled"
                : execution.ExitCode != 0 || Volatile.Read(ref _monkeyFinishedFlag) == 0 ||
                    Volatile.Read(ref _genericAbortFlag) != 0 ? "Error" : "Completed";
            if (durationExpired && execution.Cancelled && !execution.RemoteStopConfirmed) outcome = "Error";
            var reason = outcome == "Error"
                ? execution.Cancelled ? "Remote process stop could not be verified."
                    : execution.ExitCode != 0 ? $"ADB exited with code {execution.ExitCode}."
                    : Volatile.Read(ref _genericAbortFlag) != 0 ? "Monkey reported an abort."
                    : "Monkey did not print its completion marker."
                : null;
            var summary = new StressRunSummary
            {
                PackageName = package,
                DeviceName = deviceName,
                DeviceSerialHash = serialHash,
                StartedUtc = startedUtc,
                RunMode = mode,
                EventCount = mode == "Events" ? eventBudget : attempted,
                RequestedEventCount = options.EventCount,
                CycleCount = cyclesExecuted,
                EventsInjected = attempted,
                CrashCount = Volatile.Read(ref _crashCountBacking),
                AnrCount = Volatile.Read(ref _anrCountBacking),
                Duration = DateTime.UtcNow - startedUtc,
                Seed = seed,
                ThrottleMs = throttle,
                PctTouch = mix.PctTouch,
                PctMotion = mix.PctMotion,
                PctTrackball = mix.PctTrackball,
                PctNav = mix.PctNav,
                PctSyskeys = mix.PctSyskeys,
                PctAppswitch = mix.PctAppswitch,
                Outcome = outcome,
                ExitCode = execution.ExitCode,
                FailureReason = reason,
                Metrics = metrics,
                MetricSnapshots = snapshots
            };
            _lastSummary = summary;
            _lastRunSerial = device.Serial;
            AppendOutput(StressReportBuilder.BuildReport(summary).TrimEnd());
            await FlushOutputAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                CrashCount = summary.CrashCount;
                AnrCount = summary.AnrCount;
                EventsInjected = summary.EventsInjected;
                ProgressPercent = summary.Result == "PASSED" ? 100 : ProgressPercent;
                StatusMessage = $"{summary.Result}: {summary.EventsInjected:N0} events; {summary.CrashCount} crashes; {summary.AnrCount} ANRs.";
                RunHistory.Insert(0, new StressRunHistoryItem(summary));
                while (RunHistory.Count > 20) RunHistory.RemoveAt(RunHistory.Count - 1);
                UpdateComparison();
            });
            _lastRawOutput = Output;
            var failed = summary.Result is "FAILED" or "ERROR" or "INCOMPLETE";
            if (AutoSaveRuns || (CaptureFailureScreenshot && failed))
            {
                try { await SaveArtifactsAsync(summary, CaptureFailureScreenshot && failed, device.Serial); }
                catch (Exception ex)
                {
                    AppLogger.Log.Error(ex, "[StressTest] Artifact save failed");
                    StatusMessage = $"{summary.Result}; artifacts could not be saved: {ex.Message}";
                }
            }
        }
        catch (OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() => StatusMessage = "Stress test cancelled.");
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[StressTest] Run failed");
            await _dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"[!] Stress test error: {ex.Message}";
                AppendOutput($"[ERROR] {SecurityHelper.RedactSensitiveText(ex.Message)}");
            });
        }
        finally
        {
            if (profiler != null)
            {
                try { profiler.SnapshotSampled -= OnProfilerSample; await profiler.StopAsync(); profiler.Dispose(); }
                catch (Exception ex) { AppLogger.Log.Debug(ex, "[StressTest] Profiler cleanup failed"); }
            }
            _profiler = null;
            linkedCts?.Dispose();
            durationCts?.Dispose();
            if (ReferenceEquals(_runCts, userCts)) _runCts = null;
            userCts?.Dispose();
            _runningOnSerial = null;
            await _dispatcher.InvokeAsync(() => IsRunning = false);
            Interlocked.Exchange(ref _runActive, 0);
        }
    }

    private void ResetRunState()
    {
        lock (_outputLock) _outputBuffer.Clear();
        while (_pendingOutput.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _pendingOutputCount, 0);
        Output = string.Empty;
        lock (_metricSnapshots) _metricSnapshots.Clear();
        Interlocked.Exchange(ref _crashCountBacking, 0);
        Interlocked.Exchange(ref _anrCountBacking, 0);
        Interlocked.Exchange(ref _eventsAttemptedBacking, 0);
        Interlocked.Exchange(ref _genericAbortFlag, 0);
        _runOffset = 0;
        CrashCount = 0;
        AnrCount = 0;
        EventsInjected = 0;
        ProgressPercent = 0;
        LatestFps = null;
        LatestCpuPercent = null;
        LatestPssKb = null;
        LatestThermalStatus = null;
    }

    private void OnProfilerSample(ProfilerSnapshot sample)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_metricSnapshots)
        {
            if (_metricSnapshots.Count >= 3600) _metricSnapshots.RemoveAt(0);
            _metricSnapshots.Add(new MetricSnapshot
            {
                Timestamp = sample.Timestamp,
                TotalPssKb = sample.PssKb,
                CpuPercent = sample.CpuPercent,
                Fps = sample.Fps,
                ThermalStatus = sample.ThermalStatus,
                EventsInjected = Volatile.Read(ref _eventsAttemptedBacking)
            });
        }
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            LatestFps = sample.Fps;
            LatestCpuPercent = sample.CpuPercent;
            LatestPssKb = sample.PssKb;
            LatestThermalStatus = sample.ThermalStatus;
            if (_activeRunMode == "Duration" && _activeDurationSeconds > 0)
                ProgressPercent = Math.Min(100, (DateTime.UtcNow - _runStartedAt).TotalSeconds / _activeDurationSeconds * 100);
        });
    }

    [RelayCommand]
    private void StopMonkey()
    {
        if (!IsRunning) return;
        StatusMessage = "Stopping this Monkey run…";
        try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task<StressPerformanceMetrics> CollectPerformanceMetricsAsync(string serial, string packageName)
    {
        var meminfoTask = _adbService.ExecuteCommandWithResultAsync(serial, $"shell dumpsys meminfo {packageName}");
        var cpuinfoTask = _adbService.ExecuteCommandWithResultAsync(serial, "shell dumpsys cpuinfo");
        var gfxinfoTask = _adbService.ExecuteCommandWithResultAsync(serial, $"shell dumpsys gfxinfo {packageName}");
        await Task.WhenAll(meminfoTask, cpuinfoTask, gfxinfoTask);
        return StressReportBuilder.ParseMetrics(packageName,
            meminfoTask.Result.Success ? meminfoTask.Result.Output : string.Empty,
            cpuinfoTask.Result.Success ? cpuinfoTask.Result.Output : string.Empty,
            gfxinfoTask.Result.Success ? gfxinfoTask.Result.Output : string.Empty);
    }

    private void HandleOutputLine(string line)
    {
        const string marker = "Sending event #";
        var trimmed = line.TrimStart();
        var markerAt = trimmed.IndexOf(marker, StringComparison.Ordinal);
        if (markerAt >= 0 && (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith(":", StringComparison.Ordinal)))
        {
            var num = new string(trimmed[(markerAt + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(num, out var n))
            {
                var total = _runOffset + n;
                Interlocked.Exchange(ref _eventsAttemptedBacking, total);
                _dispatcher.Post(() =>
                {
                    EventsInjected = total;
                    if (_activeRunMode == "Events" && _activeEventBudget > 0)
                        ProgressPercent = Math.Min(100, (double)total / _activeEventBudget * 100);
                });
            }
        }
        if (trimmed.StartsWith("// CRASH:", StringComparison.Ordinal) || trimmed.StartsWith("Process crashed", StringComparison.Ordinal))
            Interlocked.Increment(ref _crashCountBacking);
        if (trimmed.StartsWith("// NOT RESPONDING:", StringComparison.Ordinal) || trimmed.StartsWith("ANR in", StringComparison.Ordinal))
            Interlocked.Increment(ref _anrCountBacking);
        if (trimmed.StartsWith("** Monkey aborted due to error.", StringComparison.Ordinal))
            Interlocked.Exchange(ref _genericAbortFlag, 1);
        if (trimmed.StartsWith("// Monkey finished", StringComparison.Ordinal))
            Interlocked.Exchange(ref _monkeyFinishedFlag, 1);
        if (trimmed.StartsWith("Events injected:", StringComparison.Ordinal))
        {
            var s = trimmed["Events injected:".Length..].Trim();
            if (int.TryParse(s, out var total))
            {
                var cumulative = _runOffset + total;
                Interlocked.Exchange(ref _eventsAttemptedBacking, cumulative);
                _dispatcher.Post(() => EventsInjected = cumulative);
            }
        }
        AppendOutput(line);
    }

    private int _outputDirty;

    private void AppendOutput(string line)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _pendingOutput.Enqueue(line);
        if (Interlocked.Increment(ref _pendingOutputCount) > 5000 && _pendingOutput.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingOutputCount);
        if (Interlocked.CompareExchange(ref _outputDirty, 1, 0) == 0)
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                await FlushOutputAsync();
            });
    }

    private async Task FlushOutputAsync()
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            lock (_outputLock)
            {
                while (_pendingOutput.TryDequeue(out var line))
                {
                    Interlocked.Decrement(ref _pendingOutputCount);
                    _outputBuffer.AppendLine(line);
                }
                const int maxChars = 200_000;
                if (_outputBuffer.Length > maxChars)
                {
                    _outputBuffer.Remove(0, _outputBuffer.Length - maxChars / 2);
                    _outputBuffer.Insert(0, "...[truncated]...\n");
                }
                Output = _outputBuffer.ToString();
            }
        });
        Interlocked.Exchange(ref _outputDirty, 0);
        if (!_pendingOutput.IsEmpty && Interlocked.CompareExchange(ref _outputDirty, 1, 0) == 0)
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                await FlushOutputAsync();
            });
    }

    [RelayCommand]
    private void ClearOutput()
    {
        if (IsRunning) { StatusMessage = "Stop the run before clearing its log."; return; }
        lock (_outputLock) _outputBuffer.Clear();
        while (_pendingOutput.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _pendingOutputCount, 0);
        Output = string.Empty;
        StatusMessage = "Visible output cleared; saved run results are unchanged.";
    }

    [RelayCommand]
    private async Task SaveOutputAsync()
    {
        if (_lastSummary == null) { StatusMessage = "[!] No completed run to save."; return; }
        try
        {
            await SaveArtifactsAsync(_lastSummary, false);
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[StressTest] SaveOutputAsync failed"); StatusMessage = $"[!] Save failed: {ex.Message}"; }
    }

    private async Task SaveArtifactsAsync(StressRunSummary summary, bool captureScreenshot, string? serial = null)
    {
        var configured = PreferencesService.Current.SessionsRootDirectory;
        if (string.IsNullOrWhiteSpace(configured)) configured = PathHelper.GetDefaultSessionsDirectory();
        if (!PathHelper.TryGetSafeLocalDirectory(configured, out var root))
            throw new IOException("The configured sessions directory is not safe for saving stress-test results.");
        var runsRoot = Path.Combine(root, "StressTests");
        Directory.CreateDirectory(runsRoot);
        var runDir = Path.Combine(runsRoot, $"{summary.StartedUtc:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDir);
        if (!PathHelper.RestrictDirectoryAccess(runDir))
            throw new IOException("Could not protect the stress-test artifact directory.");
        var logPath = Path.Combine(runDir, "output.log");
        var redacted = SecurityHelper.RedactSensitiveText(_lastRawOutput);
        serial ??= _lastRunSerial;
        if (!string.IsNullOrEmpty(serial)) redacted = redacted.Replace(serial, summary.DeviceSerialHash, StringComparison.Ordinal);
        await File.WriteAllTextAsync(logPath, redacted);
        var summaryPath = Path.Combine(runDir, "summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, LogProJsonContext.Default.StressRunSummary));
        var metricsPath = Path.Combine(runDir, "metrics.csv");
        var rows = new List<string> { "utc,total_pss_kb,cpu_percent,fps,thermal_status,events_injected" };
        foreach (var sample in summary.MetricSnapshots)
            rows.Add(string.Join(',', sample.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                sample.TotalPssKb?.ToString(CultureInfo.InvariantCulture) ?? "",
                sample.CpuPercent?.ToString(CultureInfo.InvariantCulture) ?? "",
                sample.Fps?.ToString(CultureInfo.InvariantCulture) ?? "",
                sample.ThermalStatus?.ToString(CultureInfo.InvariantCulture) ?? "",
                sample.EventsInjected.ToString(CultureInfo.InvariantCulture)));
        await File.WriteAllLinesAsync(metricsPath, rows);
        if (captureScreenshot && !string.IsNullOrEmpty(serial))
        {
            var screenshotPath = Path.Combine(runDir, "failure.png");
            if (!await _adbService.CaptureScreenshotAsync(serial, screenshotPath))
                AppendOutput("[!] Failure screenshot could not be captured.");
        }
        var history = RunHistory.FirstOrDefault(item => ReferenceEquals(item.Summary, summary));
        if (history != null) history.ArtifactsPath = runDir;
        StatusMessage = $"{summary.Result}: artifacts saved to {runDir}";
    }

    partial void OnSelectedBaselineRunChanged(StressRunHistoryItem? value) => UpdateComparison();

    private void UpdateComparison()
    {
        var current = _lastSummary;
        var baseline = SelectedBaselineRun?.Summary;
        if (current == null || baseline == null)
        {
            ComparisonSummary = "Choose a prior run to compare results.";
            return;
        }
        if (ReferenceEquals(current, baseline))
        {
            ComparisonSummary = "Choose a different run as the baseline.";
            return;
        }
        var parts = new List<string>
        {
            $"events {current.EventsInjected - baseline.EventsInjected:+#;-#;0}",
            $"crashes {current.CrashCount - baseline.CrashCount:+#;-#;0}",
            $"ANRs {current.AnrCount - baseline.AnrCount:+#;-#;0}"
        };
        if (current.PackageName != baseline.PackageName || current.DeviceSerialHash != baseline.DeviceSerialHash)
            parts.Insert(0, "different package or device");
        AddMetricDelta("average PSS KB", current.MetricSnapshots.Select(m => (double?)m.TotalPssKb),
            baseline.MetricSnapshots.Select(m => (double?)m.TotalPssKb), parts);
        AddMetricDelta("average CPU %", current.MetricSnapshots.Select(m => m.CpuPercent),
            baseline.MetricSnapshots.Select(m => m.CpuPercent), parts);
        AddMetricDelta("average FPS", current.MetricSnapshots.Select(m => m.Fps),
            baseline.MetricSnapshots.Select(m => m.Fps), parts);
        ComparisonSummary = $"Against {baseline.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}: {string.Join(", ", parts)}.";
    }

    private static void AddMetricDelta(string label, IEnumerable<double?> current,
        IEnumerable<double?> baseline, List<string> parts)
    {
        var currentValues = current.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var baselineValues = baseline.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (currentValues.Count > 0 && baselineValues.Count > 0)
            parts.Add($"{label} {currentValues.Average() - baselineValues.Average():+#0.0;-#0.0;0.0}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _deviceMonitor.DeviceDisconnected -= OnDeviceDisconnected;
        try { _runCts?.Cancel(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[StressTest] Dispose: cancel CTS failed"); }
        GC.SuppressFinalize(this);
    }
}

public sealed class StressRunHistoryItem
{
    public StressRunHistoryItem(StressRunSummary summary) => Summary = summary;
    public StressRunSummary Summary { get; }
    public string DisplayName => $"{Summary.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} • {Summary.PackageName} • {Summary.Result}";
    public string? ArtifactsPath { get; set; }
    public override string ToString() => DisplayName;
}
