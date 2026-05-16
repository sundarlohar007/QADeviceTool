using System.Collections.Concurrent;
using LogPro.Models;
using System.Threading;

namespace LogPro.Services;

/// <summary>
/// Background service that polls for connected devices on a timer.
/// Uses a missed-poll threshold to prevent transient USB/daemon glitches
/// from killing active log captures.
/// </summary>
public class DeviceMonitorService : IDeviceMonitorService
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private Timer? _pollTimer;
    private readonly List<DeviceInfo> _devices = new();
    private readonly object _lock = new();
    private int _isPolling;

    private readonly ConcurrentDictionary<string, int> _missedPollCount = new(StringComparer.Ordinal);
    private const int MissedPollThreshold = 3;

    public event Action<List<DeviceInfo>>? DevicesChanged;
    public event Action<DeviceInfo>? DeviceConnected;
    public event Action<DeviceInfo>? DeviceDisconnected;

    public IReadOnlyList<DeviceInfo> CurrentDevices
    {
        get { lock (_lock) return _devices.ToList(); }
    }

    public bool IsMonitoring => _pollTimer != null;

    public DeviceMonitorService(AdbService adbService, IosService iosService)
    {
        _adbService = adbService;
        _iosService = iosService;
    }

    public void StartMonitoring(int intervalMs = 10000)
    {
        StopMonitoring();
        _pollTimer = new Timer(async _ =>
        {
            try { await PollDevicesAsync(); }
            catch (Exception ex) { AppLogger.Log.Error(ex, "[DeviceMonitor] Poll timer crashed"); }
        }, null, 2000, intervalMs);
    }

    public void StopMonitoring()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    public async Task PollDevicesAsync()
    {
        if (Interlocked.Exchange(ref _isPolling, 1) != 0) return;

        try
        {
            var newDevices = new List<DeviceInfo>();

            try
            {
                var androidDevices = await _adbService.GetConnectedDevicesAsync().ConfigureAwait(false);
                newDevices.AddRange(androidDevices);
            }
            catch (Exception ex)
            {
                AppLogger.Log.Debug(ex, "Failed to get Android devices");
            }

            try
            {
                var iosDevices = await _iosService.GetConnectedDevicesAsync().ConfigureAwait(false);
                newDevices.AddRange(iosDevices);
            }
            catch (Exception ex)
            {
                AppLogger.Log.Warn(ex, "[DeviceMonitor] Failed to get iOS devices");
            }

            List<DeviceInfo> oldDevices;
            lock (_lock) { oldDevices = _devices.ToList(); }

            var newSerials = new HashSet<string>(newDevices.Select(d => d.Serial), StringComparer.Ordinal);
            var oldSerials = new HashSet<string>(oldDevices.Select(d => d.Serial), StringComparer.Ordinal);

            var connected = new List<DeviceInfo>();
            foreach (var d in newDevices)
            {
                if (!oldSerials.Contains(d.Serial))
                {
                    _missedPollCount.TryRemove(d.Serial, out _);
                    connected.Add(d);
                }
                else
                {
                    _missedPollCount.TryRemove(d.Serial, out _);
                }
            }

            var disconnected = new List<DeviceInfo>();
            foreach (var d in oldDevices)
            {
                if (!newSerials.Contains(d.Serial))
                {
                    var missed = _missedPollCount.AddOrUpdate(d.Serial, 1, (_, c) => c + 1);
                    if (missed >= MissedPollThreshold)
                    {
                        _missedPollCount.TryRemove(d.Serial, out _);
                        disconnected.Add(d);
                        AppLogger.Log.Warn($"[DeviceMonitor] Device {d.Serial} disconnected after {missed} missed polls");
                    }
                    else
                    {
                        AppLogger.Log.Debug($"[DeviceMonitor] Device {d.Serial} missed poll {missed}/{MissedPollThreshold} - not yet disconnected");
                    }
                }
            }

            lock (_lock)
            {
                _devices.Clear();
                _devices.AddRange(newDevices);
            }

            foreach (var device in connected)
                DeviceConnected?.Invoke(device);

            foreach (var device in disconnected)
                DeviceDisconnected?.Invoke(device);

            if (connected.Count > 0 || disconnected.Count > 0)
                DevicesChanged?.Invoke(newDevices);
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}