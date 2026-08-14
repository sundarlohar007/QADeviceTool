using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LogPro.Models;
using LogPro.Services;
using LogPro.Avalonia.Services;

namespace LogPro.Avalonia.ViewModels;

/// <summary>Minimal shell view-model — proves the Core engine drives an Avalonia UI (Phase 4b).</summary>
public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceMonitorService _monitor;
    private readonly IUiDispatcher _dispatcher = new AvaloniaUiDispatcher();

    public ObservableCollection<DeviceInfo> Devices { get; } = new();

    [ObservableProperty]
    private string _status = "Starting…";

    [ObservableProperty]
    private int _deviceCount;

    public string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";

    public ShellViewModel()
    {
        var adb = new AdbService();
        var ios = new IosService();
        _monitor = new DeviceMonitorService(adb, ios);
        _monitor.DevicesChanged += OnDevicesChanged;
        _monitor.StartMonitoring();
        Status = "Monitoring devices…";
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            Devices.Clear();
            foreach (var d in devices) Devices.Add(d);
            DeviceCount = Devices.Count;
            Status = DeviceCount > 0 ? $"{DeviceCount} device(s) connected" : "No devices connected";
        });
    }

    public void Dispose()
    {
        _monitor.DevicesChanged -= OnDevicesChanged;
        _monitor.Dispose();
    }
}
