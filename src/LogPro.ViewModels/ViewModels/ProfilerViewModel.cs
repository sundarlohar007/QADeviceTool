using System.Collections.ObjectModel;
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
    private AndroidPerformanceProfiler? _profiler;

    public ProfilerViewModel(IAdbService adb, IDeviceStore store, IUiDispatcher dispatcher)
    {
        _adb = adb;
        _store = store;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<ProfilerSnapshot> History { get; } = new();

    [ObservableProperty] private bool _isProfiling;
    [ObservableProperty] private string _statusMessage = "Select a device and start profiling.";
    [ObservableProperty] private double? _fps;
    [ObservableProperty] private double? _frameP90Ms;
    [ObservableProperty] private double? _cpuPercent;
    [ObservableProperty] private int? _pssKb;
    [ObservableProperty] private int? _thermalStatus;
    [ObservableProperty] private int? _batteryLevel;
    [ObservableProperty] private int _jankyFrames;

    public DeviceInfo? SelectedDevice => _store.SelectedDevice;

    [RelayCommand]
    public void StartProfiling()
    {
        if (IsProfiling) return;
        var device = _store.SelectedDevice;
        if (device == null)
        {
            StatusMessage = "Select a device first.";
            return;
        }
        if (device.Platform != DevicePlatform.Android)
        {
            StatusMessage = "Profiler currently supports Android (iOS DVT lands in phase 6).";
            return;
        }

        History.Clear();
        JankyFrames = 0;
        var package = PreferencesService.Current.TargetPackageName;
        _profiler = new AndroidPerformanceProfiler(_adb, device.Serial,
            string.IsNullOrWhiteSpace(package) ? null : package, intervalMs: 1000);
        _profiler.SnapshotSampled += OnSnapshot;
        _profiler.Start();
        IsProfiling = true;
        StatusMessage = $"Profiling {device.DisplayName}…";
    }

    [RelayCommand]
    public async Task StopProfilingAsync()
    {
        if (_profiler == null) return;
        _profiler.SnapshotSampled -= OnSnapshot;
        await _profiler.StopAsync();
        _profiler.Dispose();
        _profiler = null;
        IsProfiling = false;
        StatusMessage = $"Stopped — {History.Count} samples captured.";
    }

    private void OnSnapshot(ProfilerSnapshot snapshot)
    {
        _dispatcher.Post(() =>
        {
            Fps = snapshot.Fps;
            FrameP90Ms = snapshot.FrameTimeP90Ms;
            CpuPercent = snapshot.CpuPercent;
            PssKb = snapshot.PssKb;
            ThermalStatus = snapshot.ThermalStatus;
            BatteryLevel = snapshot.BatteryLevel;
            JankyFrames += snapshot.JankyFrames ?? 0;

            History.Add(snapshot);
            if (History.Count > 600) History.RemoveAt(0);
        });
    }

    public void Dispose()
    {
        if (_profiler != null)
        {
            _profiler.SnapshotSampled -= OnSnapshot;
            _profiler.Dispose();
            _profiler = null;
        }
        GC.SuppressFinalize(this);
    }
}
