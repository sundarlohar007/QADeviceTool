using LogPro.Models;
using System.Threading;

namespace LogPro.Services;

/// <summary>
/// Background service that polls for connected devices on a timer.
/// </summary>
public class DeviceMonitorService : IDeviceMonitorService
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private Timer? _pollTimer;
    private readonly List<DeviceInfo> _devices = new();
    private readonly object _lock = new();
    private int _isPolling;

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

    /// <summary>
    /// Starts polling for devices at the specified interval.
    /// </summary>
    public void StartMonitoring(int intervalMs = 10000)
    {
        StopMonitoring();
        _pollTimer = new Timer(async _ =>
        {
            try { await PollDevicesAsync(); }
            catch (Exception ex) { AppLogger.Log.Error(ex, "[DeviceMonitor] Poll timer crashed"); }
        }, null, 2000, intervalMs);
    }

    /// <summary>
    /// Stops device polling.
    /// </summary>
    public void StopMonitoring()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    /// <summary>
    /// Performs a single poll for all connected devices.
    /// </summary>
    public async Task PollDevicesAsync()
    {
        if (Interlocked.Exchange(ref _isPolling, 1) != 0) return;

        try
        {
            var newDevices = new List<DeviceInfo>();

            // Get Android devices
            try
            {
                var androidDevices = await _adbService.GetConnectedDevicesAsync().ConfigureAwait(false);
                newDevices.AddRange(androidDevices);
            }
            catch (Exception ex)
            {
                AppLogger.Log.Debug(ex, "Failed to get Android devices");
            }

            // Get iOS devices
            try
            {
                var iosDevices = await _iosService.GetConnectedDevicesAsync().ConfigureAwait(false);
                newDevices.AddRange(iosDevices);
            }
            catch (Exception ex)
            {
                AppLogger.Log.Warn(ex, "[DeviceMonitor] Failed to get iOS devices");
            }

            // Detect changes
            List<DeviceInfo> oldDevices;
            lock (_lock)
            {
                oldDevices = _devices.ToList();
            }

            var connected = newDevices.Where(n => !oldDevices.Any(o => o.Serial == n.Serial)).ToList();
            var disconnected = oldDevices.Where(o => !newDevices.Any(n => n.Serial == o.Serial)).ToList();

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
