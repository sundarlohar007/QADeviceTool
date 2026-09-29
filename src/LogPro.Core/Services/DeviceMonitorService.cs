using System.Collections.Concurrent;
using LogPro.Helpers;
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
    private int _disposed;

    private readonly ConcurrentDictionary<(DevicePlatform Platform, string Serial), int> _missedPollCount = new();
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
        if (Volatile.Read(ref _disposed) != 0) return;
        StopMonitoring();
        _pollTimer = new Timer(async _ =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
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
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _isPolling, 1) != 0) return;

        try
        {
            var newDevices = new List<DeviceInfo>();


            // Poll Android and iOS in parallel
            var androidTask = _adbService.GetConnectedDevicesWithStatusAsync();
            var iosTask = _iosService.GetConnectedDevicesAsync();
            List<DeviceInfo> oldDevices;
            lock (_lock) { oldDevices = _devices.ToList(); }

            try
            {
                var android = await androidTask.ConfigureAwait(false);
                if (android.Success)
                    newDevices.AddRange(android.Devices);
                else
                {
                    AppLogger.Log.Warn("[DeviceMonitor] ADB discovery failed; retaining the previous Android device state");
                    newDevices.AddRange(oldDevices.Where(d => d.Platform == DevicePlatform.Android));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log.Warn(ex, "[DeviceMonitor] Failed to get Android devices");
                newDevices.AddRange(oldDevices.Where(d => d.Platform == DevicePlatform.Android));
            }

            try { newDevices.AddRange(await iosTask.ConfigureAwait(false)); }
            catch (Exception ex)
            {
                AppLogger.Log.Warn(ex, "[DeviceMonitor] Failed to get iOS devices");
                newDevices.AddRange(oldDevices.Where(d => d.Platform == DevicePlatform.iOS));
            }

            var newSerials = new HashSet<(DevicePlatform, string)>(newDevices.Select(d => (d.Platform, d.Serial)));
            var oldSerials = new HashSet<(DevicePlatform, string)>(oldDevices.Select(d => (d.Platform, d.Serial)));

            var connected = new List<DeviceInfo>();
            foreach (var d in newDevices)
            {
                if (!oldSerials.Contains((d.Platform, d.Serial)))
                {
                    _missedPollCount.TryRemove((d.Platform, d.Serial), out _);
                    connected.Add(d);
                }
                else
                {
                    _missedPollCount.TryRemove((d.Platform, d.Serial), out _);
                }
            }

            var disconnected = new List<DeviceInfo>();
            var missedPollDevices = new List<DeviceInfo>();
            foreach (var d in oldDevices)
            {
                if (!newSerials.Contains((d.Platform, d.Serial)))
                {
                    var missed = _missedPollCount.AddOrUpdate((d.Platform, d.Serial), 1, (_, c) => c + 1);
                    if (missed >= MissedPollThreshold)
                    {
                        _missedPollCount.TryRemove((d.Platform, d.Serial), out _);
                        disconnected.Add(d);
                        AppLogger.Log.Warn($"[DeviceMonitor] Device {SecurityHelper.HashSerial(d.Serial)} disconnected after {missed} missed polls");
                    }
                    else
                    {
                        missedPollDevices.Add(d);
                        AppLogger.Log.Debug($"[DeviceMonitor] Device {SecurityHelper.HashSerial(d.Serial)} missed poll {missed}/{MissedPollThreshold} - not yet disconnected");
                    }
                }
            }

            bool changedProperties = false;
            foreach (var nd in newDevices)
            {
                var od = oldDevices.FirstOrDefault(o => o.Serial == nd.Serial && o.Platform == nd.Platform);
                if (od != null && (od.ConnectionState != nd.ConnectionState || od.BatteryLevel != nd.BatteryLevel || od.BatteryStatus != nd.BatteryStatus || od.Name != nd.Name || od.Model != nd.Model || od.OsVersion != nd.OsVersion || od.Product != nd.Product || od.Manufacturer != nd.Manufacturer || od.UsbInfo != nd.UsbInfo || od.Notes != nd.Notes || od.Tag != nd.Tag))
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

            if (Volatile.Read(ref _disposed) != 0) return;

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopMonitoring();
    }
}
