using LogPro.Models;

namespace LogPro.Services;

/// <summary>Independent platform discovery with bounded stale-state retention.</summary>
public class DeviceMonitorService : IDeviceMonitorService
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly object _lock = new();
    private readonly object _pollGate = new();
    private readonly List<DeviceInfo> _devices = new();
    private readonly Dictionary<(DevicePlatform, string), int> _misses = new();
    private readonly Dictionary<DevicePlatform, string> _errors = new();
    private Task? _androidPoll, _iosPoll, _activePoll;
    private Timer? _pollTimer;
    private int _disposed, _generation;
    public event Action<List<DeviceInfo>>? DevicesChanged;
    public event Action<DeviceInfo>? DeviceConnected;
    public event Action<DeviceInfo>? DeviceDisconnected;
    public event Action<string?>? DiscoveryStatusChanged;
    public IReadOnlyList<DeviceInfo> CurrentDevices { get { lock (_lock) return _devices.ToList(); } }
    public bool IsMonitoring => _pollTimer != null;
    public string? LastDiscoveryError { get; private set; }

    public DeviceMonitorService(IAdbService adbService, IIosService iosService)
    {
        _adbService = adbService;
        _iosService = iosService;
    }

    public void StartMonitoring(int intervalMs = 10000)
    {
        if (_disposed != 0) return;
        StopMonitoring();
        _pollTimer = new Timer(_ => _ = PollDevicesAsync(), null, 0, Math.Max(1000, intervalMs));
    }

    public void StopMonitoring()
    {
        Interlocked.Increment(ref _generation);
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    public Task PollDevicesAsync()
    {
        lock (_pollGate)
        {
            if (_disposed != 0) return Task.CompletedTask;
            // A slow iOS poll must not prevent subsequent Android refreshes.
            var started = false;
            if (_androidPoll == null || _androidPoll.IsCompleted)
            {
                _androidPoll = PollPlatformAsync(DevicePlatform.Android, _adbService.GetConnectedDevicesWithStatusAsync, _generation);
                started = true;
            }
            if (_iosPoll == null || _iosPoll.IsCompleted)
            {
                _iosPoll = PollPlatformAsync(DevicePlatform.iOS, _iosService.GetConnectedDevicesWithStatusAsync, _generation);
                started = true;
            }
            if (started || _activePoll == null) _activePoll = Task.WhenAll(_androidPoll, _iosPoll);
            return _activePoll;
        }
    }

    private async Task PollPlatformAsync(DevicePlatform platform,
        Func<Task<(bool Success, List<DeviceInfo> Devices)>> discover, int generation)
    {
        // Prevent synchronous discovery callbacks from reentering PollDevicesAsync before task assignment.
        await Task.Yield();
        (bool Success, List<DeviceInfo> Devices) result;
        try { result = await discover().ConfigureAwait(false); }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, $"[{platform}] Device discovery failed");
            result = (false, new());
        }
        lock (_lock)
        {
            if (_disposed != 0 || generation != _generation) return;
            var old = _devices.Where(d => d.Platform == platform).ToList();
            var next = result.Success ? result.Devices.DistinctBy(d => d.Serial).ToList()
                : old.Select(d => d.WithTemporaryUnavailable(true)).ToList();
            var connected = next.Where(d => old.All(o => o.Serial != d.Serial) ||
                (d.IsReady && old.Any(o => o.Serial == d.Serial && !o.IsReady))).ToList();
            var disconnected = new List<DeviceInfo>();
            if (result.Success)
            {
                _errors.Remove(platform);
                foreach (var device in next) _misses.Remove((platform, device.Serial));
                foreach (var device in old.Where(o => next.All(d => d.Serial != o.Serial)))
                {
                    var key = (platform, device.Serial);
                    _misses.TryGetValue(key, out var missed);
                    if (++missed >= 3) { disconnected.Add(device); _misses.Remove(key); }
                    else { _misses[key] = missed; next.Add(device.WithTemporaryUnavailable(true)); }
                }
            }
            else _errors[platform] = platform == DevicePlatform.Android
                ? "Android discovery failed. Check ADB, USB debugging, authorization and the USB driver."
                : "iOS discovery failed. Check pymobiledevice3, Apple Mobile Device Service and device trust.";

            var error = _errors.Count == 0 ? null : string.Join(" ", _errors.OrderBy(e => e.Key).Select(e => e.Value));
            if (error != LastDiscoveryError) { LastDiscoveryError = error; Publish(DiscoveryStatusChanged, error); }
            var changed = old.Count != next.Count || old.Zip(next).Any(pair => !Same(pair.First, pair.Second));
            _devices.RemoveAll(d => d.Platform == platform);
            _devices.AddRange(next);
            foreach (var device in connected) Publish(DeviceConnected, device);
            foreach (var device in disconnected) Publish(DeviceDisconnected, device);
            if (changed) Publish(DevicesChanged, _devices.ToList());
        }
    }

    private static bool Same(DeviceInfo a, DeviceInfo b) => a.Serial == b.Serial && a.Platform == b.Platform &&
        a.ConnectionState == b.ConnectionState && a.IsTemporarilyUnavailable == b.IsTemporarilyUnavailable &&
        a.Name == b.Name && a.Model == b.Model && a.OsVersion == b.OsVersion && a.Product == b.Product &&
        a.BatteryLevel == b.BatteryLevel && a.BatteryStatus == b.BatteryStatus && a.Manufacturer == b.Manufacturer &&
        a.UsbInfo == b.UsbInfo && a.Notes == b.Notes && a.Tag == b.Tag;

    private static void Publish<T>(Action<T>? handlers, T value)
    {
        if (handlers == null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception ex) { AppLogger.Log.Warn(ex, "Device observer failed"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) StopMonitoring();
    }
}
