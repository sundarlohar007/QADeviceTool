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
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
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

    public DeviceMonitorService(IAdbService adbService, IIosService iosService)
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


            // Poll Android and iOS in parallel
            var androidTask = _adbService.GetConnectedDevicesAsync();
            var iosTask = _iosService.GetConnectedDevicesAsync();
            await Task.WhenAll(androidTask, iosTask).ConfigureAwait(false);

            try { newDevices.AddRange(androidTask.Result); }
            catch (Exception ex) { AppLogger.Log.Warn(ex, "[DeviceMonitor] Failed to get Android devices"); }

            try { newDevices.AddRange(iosTask.Result); }
            catch (Exception ex) { AppLogger.Log.Warn(ex, "[DeviceMonitor] Failed to get iOS devices"); }

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
            var missedPollDevices = new List<DeviceInfo>();
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
                        missedPollDevices.Add(d);
                        AppLogger.Log.Debug($"[DeviceMonitor] Device {d.Serial} missed poll {missed}/{MissedPollThreshold} - not yet disconnected");
                    }
                }
            }

            bool changedProperties = false;
            foreach (var nd in newDevices)
            {
                var od = oldDevices.FirstOrDefault(o => o.Serial == nd.Serial);
                if (od != null && (od.ConnectionState != nd.ConnectionState || od.BatteryLevel != nd.BatteryLevel || od.Name != nd.Name))
                {
                    changedProperties = true;
                }
            }

            List<DeviceInfo> finalDevices;
            lock (_lock)
            {
                _devices.Clear();
                _devices.AddRange(newDevices);
                _devices.AddRange(missedPollDevices);
                finalDevices = _devices.ToList();
            }

            foreach (var device in connected)
                DeviceConnected?.Invoke(device);

            foreach (var device in disconnected)
                DeviceDisconnected?.Invoke(device);

            if (connected.Count > 0 || disconnected.Count > 0 || changedProperties)
                DevicesChanged?.Invoke(finalDevices);
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
