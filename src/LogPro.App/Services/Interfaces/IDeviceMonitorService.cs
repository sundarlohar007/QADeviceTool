using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Interface for device monitoring and polling.
/// </summary>
public interface IDeviceMonitorService : IDisposable
{
    event Action<List<DeviceInfo>>? DevicesChanged;
    event Action<DeviceInfo>? DeviceConnected;
    event Action<DeviceInfo>? DeviceDisconnected;

    IReadOnlyList<DeviceInfo> CurrentDevices { get; }
    bool IsMonitoring { get; }

    void StartMonitoring(int intervalMs = 10000);
    void StopMonitoring();
    Task PollDevicesAsync();
}
