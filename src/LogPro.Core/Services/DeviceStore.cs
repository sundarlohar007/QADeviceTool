using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Observable device list + selection store. Owns no polling; callers feed it via
/// <see cref="UpdateDevices"/> (typically from IDeviceMonitorService.DevicesChanged).
/// </summary>
public sealed class DeviceStore : IDeviceStore
{
    private readonly IUiDispatcher _dispatcher;
    private readonly object _lock = new();
    private List<DeviceInfo> _devices = new();
    private DeviceInfo? _selected;

    public event Action? Changed;

    public DeviceStore(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public IReadOnlyList<DeviceInfo> Devices
    {
        get { lock (_lock) return _devices.ToList(); }
    }

    public DeviceInfo? SelectedDevice
    {
        get { lock (_lock) return _selected; }
        set
        {
            bool changed;
            lock (_lock)
            {
                changed = !ReferenceEquals(_selected, value) && (_selected?.Serial != value?.Serial || _selected?.Platform != value?.Platform);
                if (changed) _selected = value;
            }
            if (changed) RaiseChanged();
        }
    }

    public void UpdateDevices(IReadOnlyList<DeviceInfo> devices)
    {
        bool listChanged;
        lock (_lock)
        {
            listChanged = _devices.Count != devices.Count ||
                Enumerable.Range(0, Math.Min(_devices.Count, devices.Count))
                    .Any(i => !SameDeviceAndMetadata(_devices[i], devices[i]));
            _devices = devices.ToList();

            // Preserve selection while connected; auto-select first otherwise.
            var selection = _selected == null ? null : _devices.FirstOrDefault(d => d.Serial == _selected.Serial && d.Platform == _selected.Platform);
            selection ??= _devices.FirstOrDefault();
            if (selection?.Serial != _selected?.Serial || selection?.Platform != _selected?.Platform) listChanged = true;
            _selected = selection;
        }
        if (listChanged) RaiseChanged();
    }

    private static bool SameDeviceAndMetadata(DeviceInfo a, DeviceInfo b) =>
        a.Serial == b.Serial && a.Platform == b.Platform && a.Name == b.Name &&
        a.Model == b.Model && a.Product == b.Product && a.ConnectionState == b.ConnectionState &&
        a.BatteryLevel == b.BatteryLevel && a.BatteryStatus == b.BatteryStatus &&
        a.OsVersion == b.OsVersion && a.Manufacturer == b.Manufacturer && a.UsbInfo == b.UsbInfo &&
        a.Notes == b.Notes && a.Tag == b.Tag;

    public void Dispose()
    {
        Changed = null;
    }

    private void RaiseChanged()
    {
        if (_dispatcher.IsOnUiThread)
        {
            Changed?.Invoke();
        }
        else
        {
            _dispatcher.Post(() => Changed?.Invoke());
        }
    }
}
