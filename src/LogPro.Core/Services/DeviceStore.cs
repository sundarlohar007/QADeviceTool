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
                changed = !ReferenceEquals(_selected, value) && _selected?.Serial != value?.Serial;
                if (changed) _selected = value;
            }
            if (changed) RaiseChanged();
        }
    }

    public void UpdateDevices(IReadOnlyList<DeviceInfo> devices)
    {
        DeviceInfo? selection;
        bool listChanged;
        lock (_lock)
        {
            var serials = new HashSet<string>(devices.Select(d => d.Serial), StringComparer.Ordinal);
            listChanged = _devices.Count != devices.Count || _devices.Any(d => !serials.Contains(d.Serial));
            _devices = devices.ToList();

            // Preserve selection while connected; auto-select first otherwise.
            var stillConnected = _selected != null && serials.Contains(_selected.Serial);
            if (stillConnected)
            {
                selection = _selected;
            }
            else
            {
                selection = _devices.FirstOrDefault();
                if (!ReferenceEquals(selection, _selected)) listChanged = true;
                _selected = selection;
            }
        }
        if (listChanged) RaiseChanged();
    }

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