using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;
using QADeviceTool.Helpers;
using System.Linq;

namespace QADeviceTool.ViewModels;

public partial class VitalsViewModel : ObservableObject
{
    private readonly AdbService _adbService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer _pollTimer;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _devices = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _memInfoOutput = "Select a device and start polling.";

    [ObservableProperty]
    private string _topProcessesOutput = string.Empty;

    [ObservableProperty]
    private bool _isPolling;

    public VitalsViewModel(AdbService adbService, DeviceMonitorService deviceMonitor)
    {
        _adbService = adbService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = Application.Current.Dispatcher;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pollTimer.Tick += async (s, e) => await PollVitalsAsync();

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Invoke(() =>
        {
            var currentSelected = SelectedDevice?.Serial;
            
            Devices.Clear();
            foreach (var d in devices)
            {
                if (d.Platform == DevicePlatform.Android) // Diagnostics heavily lean Android CLI
                {
                    Devices.Add(d);
                }
            }
                
            if (!string.IsNullOrEmpty(currentSelected))
            {
                SelectedDevice = Devices.FirstOrDefault(d => d.Serial == currentSelected);
            }
            if (SelectedDevice == null && Devices.Count > 0)
            {
                SelectedDevice = Devices.First();
            }
        });
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value == null)
        {
            StopPolling();
            MemInfoOutput = string.Empty;
            TopProcessesOutput = string.Empty;
        }
        else if (IsPolling)
        {
            // Immediately poll the new device
            _ = PollVitalsAsync();
        }
    }

    [RelayCommand]
    private void TogglePolling()
    {
        if (IsPolling) StopPolling();
        else StartPolling();
    }

    private void StartPolling()
    {
        if (SelectedDevice == null) return;
        IsPolling = true;
        _ = PollVitalsAsync(); // initial poll
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        IsPolling = false;
        _pollTimer.Stop();
    }

    private async Task PollVitalsAsync()
    {
        if (SelectedDevice == null || SelectedDevice.Platform != DevicePlatform.Android) return;

        try
        {
            // 1. Get memory info (summary only)
            var memResult = await ToolLauncher.RunAsync("adb", $"-s {SelectedDevice.Serial} shell dumpsys meminfo", 5000);
            
            // 2. Get top processes (one iteration, batch mode)
            var topResult = await ToolLauncher.RunAsync("adb", $"-s {SelectedDevice.Serial} shell top -b -n 1", 5000);

            _dispatcher.Invoke(() =>
            {
                if (memResult.Success)
                {
                    // For UI compactness, we might just show the 'Total PSS by process' or bottom summary
                    var lines = memResult.Output.Split('\n');
                    var summaryLines = lines.SkipWhile(l => !l.Contains("Total RAM")).ToList();
                    MemInfoOutput = summaryLines.Count > 0 
                        ? string.Join("\n", summaryLines).Trim() 
                        : "No memory summary available.";
                }

                if (topResult.Success)
                {
                    // Take the first 15 lines of top output
                    var lines = topResult.Output.Split('\n').Take(15);
                    TopProcessesOutput = string.Join("\n", lines).Trim();
                }
            });
        }
        catch 
        {
            // Ignore temporary polling errors
        }
    }
}
