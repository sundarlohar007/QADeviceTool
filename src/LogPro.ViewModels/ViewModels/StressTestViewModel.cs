using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

public partial class StressTestViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly List<object> _metricSnapshots = new();
    private readonly IUiDispatcher _dispatcher;

    private CancellationTokenSource? _runCts;
    private Process? _adbProcess;
    private string? _runningOnSerial;
    private DateTime _runStartedAt;
    private List<AppItem> _allApps = new();

    [ObservableProperty] private ObservableCollection<DeviceInfo> _devices = new();
    [ObservableProperty] private DeviceInfo? _selectedDevice;

    [ObservableProperty] private ObservableCollection<AppItem> _filteredApps = new();
    [ObservableProperty] private AppItem? _selectedApp;
    [ObservableProperty] private string _appSearchQuery = string.Empty;
    [ObservableProperty] private bool _isLoadingApps;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isRunning;

    public bool CanRun => IsPlatformSupported && !IsRunning;
    private readonly System.Text.StringBuilder _outputBuffer = new();
    public string Output { get; set; } = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _crashCount;
    [ObservableProperty] private int _anrCount;
    [ObservableProperty] private int _eventsInjected;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _platformBadge = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isPlatformSupported;

    public StressTestViewModel(IAdbService adbService, IDeviceMonitorService deviceMonitor, IUiDispatcher? dispatcher = null)
    {
        _adbService = adbService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;

        TargetPackage = PreferencesService.Current.TargetPackageName;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        _deviceMonitor.DeviceDisconnected += OnDeviceDisconnected;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            Devices.Clear();
            foreach (var d in devices) Devices.Add(d);
            SelectedDevice ??= Devices.FirstOrDefault(d => d.Platform == DevicePlatform.Android) ?? Devices.FirstOrDefault();
        });
    }

    private void OnDeviceDisconnected(DeviceInfo device)
    {
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
        if (Devices.Any(d => d.Serial == device.Serial)) SelectedDevice = device;
    }



    [RelayCommand]
    public async Task RefreshAppsAsync()
    {
        if (SelectedDevice == null || SelectedDevice.Platform != DevicePlatform.Android) return;
        IsLoadingApps = true;
        StatusMessage = "Loading installed apps...";
        try
        {
            var apps = await _adbService.ListInstalledAppsAsync(SelectedDevice.Serial);
            _allApps = apps;
            ApplyAppFilter();
            StatusMessage = $"Loaded {_allApps.Count} apps. Type to search.";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[StressTest] RefreshAppsAsync failed"); StatusMessage = $"[!] Load apps failed: {ex.Message}"; }
        finally { IsLoadingApps = false; }
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
    private async Task RunMonkeyAsync()
    {
        if (IsRunning) return;
        if (SelectedDevice == null)
        {
            StatusMessage = "[!] No device selected.";
            return;
        }
        if (SelectedDevice.Platform != DevicePlatform.Android)
        {
            StatusMessage = "[!] Monkey is Android-only — pymobiledevice3 has no equivalent on iOS.";
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetPackage))
        {
            StatusMessage = "[!] Search and pick an app, or type a package name.";
            return;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(TargetPackage, @"^[a-zA-Z0-9._]+$"))
        {
            StatusMessage = "[!] Invalid package name. Letters, numbers, dots, underscores only.";
            return;
        }
        if (EventCount <= 0)
        {
            StatusMessage = "[!] Event count must be > 0.";
            return;
        }
        if (EventCount > 1_000_000)
        {
            StatusMessage = "[!] Event count capped at 1,000,000.";
            EventCount = 1_000_000;
        }
        else if (EventCount > 100_000)
        {
            AppendOutput($"[i] Large run: {EventCount:N0} events may take a long time.");
        }
        // Validate monkey event percentages sum to 100
        var totalPct = PctTouch + PctMotion + PctTrackball + PctNav + PctSyskeys + PctAppswitch;
        if (totalPct != 100)
        {
            StatusMessage = $"[!] Event percentages must sum to 100%. Current total: {totalPct}%.";
            return;
        }

        // Verify package actually exists on device — auto-resolve case-insensitive partial name.
        if (_allApps.Count > 0 && !_allApps.Any(a => a.PackageId == TargetPackage))
        {
            var match = _allApps.FirstOrDefault(a => a.PackageId.Equals(TargetPackage, StringComparison.OrdinalIgnoreCase))
                     ?? _allApps.FirstOrDefault(a => a.PackageId.Contains(TargetPackage, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                AppendOutput($"[i] Resolved '{TargetPackage}' → '{match.PackageId}'");
                TargetPackage = match.PackageId;
            }
            else
            {
                StatusMessage = $"[!] Package '{TargetPackage}' not installed on device.";
                return;
            }
        }

        _runCts = new CancellationTokenSource();
        IsRunning = true;
        _metricSnapshots.Clear();
        var metricsTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (_runningOnSerial == null || _adbService == null) return;
                var memResult = await _adbService.ExecuteCommandAsync(_runningOnSerial, "shell dumpsys meminfo " + TargetPackage);
                var snapshot = new Services.MetricSnapshot { Timestamp = DateTime.Now, EventsInjected = EventsInjected };
                var pssMatch = System.Text.RegularExpressions.Regex.Match(memResult, @"TOTAL\s+(\d+)");
                if (pssMatch.Success && int.TryParse(pssMatch.Groups[1].Value, out var pss))
                    snapshot.TotalPssKb = pss;
                lock (_metricSnapshots) { _metricSnapshots.Add(snapshot); }
            }
            catch { /* metrics best-effort */ }
        }, null, 5000, 5000);
        CrashCount = 0;
        AnrCount = 0;
        EventsInjected = 0;
        ProgressPercent = 0;
        Output = string.Empty;
        _runningOnSerial = SelectedDevice.Serial;
        _runStartedAt = DateTime.Now;

        var args = $"-s \"{SelectedDevice.Serial}\" shell monkey -p {TargetPackage} " +
                   $"-v -v --throttle {ThrottleMs} -s {Seed} --pct-touch {PctTouch} " +
                   $"--pct-motion {PctMotion} --pct-trackball {PctTrackball} --pct-nav {PctNav} " +
                   $"--pct-syskeys {PctSyskeys} --pct-appswitch {PctAppswitch} {EventCount}";

        StatusMessage = $"Running monkey on {TargetPackage} ({EventCount} events)...";
        AppendOutput($"$ adb {args}\n");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ToolResolver.Resolve("adb"),
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) HandleOutputLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) HandleOutputLine(e.Data); };

            process.Start();
            ProcessManagerService.TrackProcess(process);
            _adbProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(_runCts.Token).ConfigureAwait(false);

            var duration = DateTime.Now - _runStartedAt;
            var metrics = await CollectPerformanceMetricsAsync(SelectedDevice.Serial, TargetPackage).ConfigureAwait(false);
            var report = StressReportBuilder.BuildReport(new StressRunSummary
            {
                PackageName = TargetPackage,
                DeviceName = SelectedDevice.DisplayName,
                EventCount = EventCount,
                EventsInjected = EventsInjected,
                CrashCount = CrashCount,
                AnrCount = AnrCount,
                Duration = duration,
                Metrics = metrics
            });
            AppendOutput("");
            AppendOutput(report.TrimEnd());

            await _dispatcher.InvokeAsync(() =>
            {
                if (!_runCts.IsCancellationRequested)
                {
                    StatusMessage = $"Done. {EventsInjected}/{EventCount} events. Crashes: {CrashCount} ANRs: {AnrCount}";
                    ProgressPercent = EventCount > 0 ? (double)EventsInjected / EventCount * 100 : 100;
                }
            });
        }
        catch (OperationCanceledException)
        {
            await KillOnDeviceMonkeyAsync(_runningOnSerial);
            await _dispatcher.InvokeAsync(() => StatusMessage = "Cancelled. On-device monkey killed.");
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[StressTest] RunMonkeyAsync failed");
            await _dispatcher.InvokeAsync(() => { StatusMessage = $"[!] Error: {ex.Message}"; AppendOutput($"\nERROR: {ex.Message}"); });
        }
        finally
        {
            try { _adbProcess?.Dispose(); } catch { /* best effort */ }
            _adbProcess = null;
            _runningOnSerial = null;
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void StopMonkey()
    {
        if (!IsRunning) return;
        StatusMessage = "Stopping monkey...";
        var serialAtStop = _runningOnSerial;

        // Cancel waiter, kill local adb process tree, then kill on-device monkey.
        try { _runCts?.Cancel(); } catch { /* best effort */ }
        try
        {
            if (_adbProcess != null && !_adbProcess.HasExited)
                _adbProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[StressTest] Local kill failed"); }

        // Fire-and-forget on-device kill so STOP returns instantly.
        _ = Task.Run(async () => await KillOnDeviceMonkeyAsync(serialAtStop));
    }

    /// <summary>
    /// Issues `adb shell pkill com.android.commands.monkey` on the device.
    /// Without this, monkey continues running on the device after the host adb client exits
    /// (it survives device unplug too — the on-device process is independent).
    /// </summary>
    private async Task KillOnDeviceMonkeyAsync(string? serial)
    {
        if (string.IsNullOrEmpty(serial)) return;
        try
        {
            // Multiple kill paths — different OEMs allow different signal modes.
            await _adbService.ExecuteCommandAsync(serial, "shell pkill -l 9 com.android.commands.monkey");
            await _adbService.ExecuteCommandAsync(serial, "shell pkill -f monkey");
            await _adbService.ExecuteCommandAsync(serial, "shell killall -9 com.android.commands.monkey");
            await _dispatcher.InvokeAsync(() => AppendOutput("[STOP] On-device monkey terminated."));
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[StressTest] On-device kill error");
        }
    }

    private async Task<StressPerformanceMetrics> CollectPerformanceMetricsAsync(string serial, string packageName)
    {
        try
        {
            var meminfoTask = _adbService.ExecuteCommandAsync(serial, $"shell dumpsys meminfo {packageName}");
            var cpuinfoTask = _adbService.ExecuteCommandAsync(serial, "shell dumpsys cpuinfo");
            var gfxinfoTask = _adbService.ExecuteCommandAsync(serial, $"shell dumpsys gfxinfo {packageName}");

            await Task.WhenAll(meminfoTask, cpuinfoTask, gfxinfoTask).ConfigureAwait(false);
            return StressReportBuilder.ParseMetrics(packageName, meminfoTask.Result, cpuinfoTask.Result, gfxinfoTask.Result);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[StressTest] Failed to collect performance metrics");
            return new StressPerformanceMetrics();
        }
    }

    private void HandleOutputLine(string line)
    {
        // Parse progress markers: ":Sending event #1234"
        if (line.Contains(":Sending event #"))
        {
            var idx = line.IndexOf(":Sending event #") + ":Sending event #".Length;
            var rest = line.Substring(idx).Trim();
            var spaceAt = rest.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            var num = spaceAt > 0 ? rest.Substring(0, spaceAt) : rest;
            if (int.TryParse(num, out var n))
            {
                EventsInjected = n;
                if (EventCount > 0)
                    ProgressPercent = Math.Min(100, (double)n / EventCount * 100);
            }
        }

        // Crash + ANR detection (line-anchored to avoid false positives in payload).
        if (line.Contains("// CRASH:") || line.Contains("** Monkey aborted due to error.") || line.Contains("Process crashed"))
            CrashCount++;
        if (line.Contains("// NOT RESPONDING:") || line.Contains("ANR in"))
            AnrCount++;

        // Final summary
        if (line.StartsWith("Events injected:"))
        {
            var s = line.Substring("Events injected:".Length).Trim();
            if (int.TryParse(s, out var total)) EventsInjected = total;
        }

        AppendOutput(line);
    }

    private void AppendOutput(string line)
    {
        _dispatcher.Post(() =>
        {
            // Cap output buffer at ~200KB to prevent UI sluggishness during long runs.
            const int MaxChars = 200_000;
            if (Output.Length > MaxChars)
                Output = "...[truncated]...\n" + Output.Substring(Output.Length - MaxChars / 2);
            _outputBuffer.AppendLine(line); Output = _outputBuffer.ToString();
        });
    }

    [RelayCommand]
    private void ClearOutput()
    {
        Output = string.Empty;
        CrashCount = 0;
        AnrCount = 0;
        EventsInjected = 0;
        ProgressPercent = 0;
        StatusMessage = "Cleared.";
    }

    [RelayCommand]
    private async Task SaveOutputAsync()
    {
        if (string.IsNullOrEmpty(Output)) { StatusMessage = "[!] Nothing to save."; return; }
        try
        {
            var dir = PathHelper.GetDefaultSessionsDirectory();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safePkg = string.IsNullOrEmpty(TargetPackage) ? "monkey" : TargetPackage.Replace('.', '_');
            var path = Path.Combine(dir, $"monkey_{safePkg}_{stamp}.log");
            var header = $"# LogPro monkey run\n# Device: {SelectedDevice?.DisplayName} ({SelectedDevice?.Serial})\n" +
                         $"# Package: {TargetPackage}\n# Events: {EventCount}  Seed: {Seed}  Throttle: {ThrottleMs}ms\n" +
                         $"# Crashes: {CrashCount}  ANRs: {AnrCount}  Events injected: {EventsInjected}\n\n";
            await File.WriteAllTextAsync(path, header + Output);
            StatusMessage = $"Saved → {path}";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[StressTest] SaveOutputAsync failed"); StatusMessage = $"[!] Save failed: {ex.Message}"; }
    }

    public void Dispose()
    {
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _deviceMonitor.DeviceDisconnected -= OnDeviceDisconnected;
        GC.SuppressFinalize(this);
    }
}
