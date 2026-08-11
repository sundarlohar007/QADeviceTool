using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Single observable source of truth for connected devices and the selected device.
/// Replaces the per-ViewModel duplicated list/selection logic and the manual fan-out
/// in MainViewModel. Implementations must marshal events to the UI thread.
/// </summary>
public interface IDeviceStore : IDisposable
{
    /// <summary>Raises when the device list or selected device changes. Always raised on the UI thread.</summary>
    event Action? Changed;

    IReadOnlyList<DeviceInfo> Devices { get; }
    DeviceInfo? SelectedDevice { get; set; }

    /// <summary>Replaces the device list, preserving selection when the device is still connected.</summary>
    void UpdateDevices(IReadOnlyList<DeviceInfo> devices);
}