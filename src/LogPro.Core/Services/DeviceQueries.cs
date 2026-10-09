using LogPro.Models;

namespace LogPro.Services;

/// <summary>Cancellable queries supported by production device services; legacy adapters remain compatible.</summary>
public interface ICancellableDeviceQueries
{
    Task<List<DeviceInfo>> GetConnectedDevicesAsync(CancellationToken token);
    Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device, CancellationToken token);
}

public static class DeviceQueries
{
    public static Task<List<DeviceInfo>> DiscoverAsync(IAdbService service, CancellationToken token) =>
        service is ICancellableDeviceQueries queries ? queries.GetConnectedDevicesAsync(token) : service.GetConnectedDevicesAsync().WaitAsync(token);
    public static Task<List<DeviceInfo>> DiscoverAsync(IIosService service, CancellationToken token) =>
        service is ICancellableDeviceQueries queries ? queries.GetConnectedDevicesAsync(token) : service.GetConnectedDevicesAsync().WaitAsync(token);
    public static Task<DeviceInfo> DetailsAsync(IAdbService service, DeviceInfo device, CancellationToken token) =>
        service is ICancellableDeviceQueries queries ? queries.GetDeviceDetailsAsync(device, token) : service.GetDeviceDetailsAsync(device).WaitAsync(token);
    public static Task<DeviceInfo> DetailsAsync(IIosService service, DeviceInfo device, CancellationToken token) =>
        service is ICancellableDeviceQueries queries ? queries.GetDeviceDetailsAsync(device, token) : service.GetDeviceDetailsAsync(device).WaitAsync(token);
}
