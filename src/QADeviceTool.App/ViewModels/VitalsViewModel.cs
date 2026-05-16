using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;
using LogPro.Helpers;
using System.Linq;

namespace LogPro.ViewModels;

public partial class VitalsViewModel : ObservableObject
{
    private readonly AdbService _adbService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _pollTimer;
    private CancellationTokenSource? _pollCts;

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
        _pollTimer.Tick += (s, e) =>
        {
            try
            {
                if (IsPolling) _ = PollVitalsAsync().ContinueWith(
                    t => Services.AppLogger.Log.Debug(t.Exception, "[Vitals] Poll failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[Vitals] Timer poll failed"); }
        };

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var currentSelected = SelectedDevice?.Serial;
            
            Devices.Clear();
            foreach (var d in devices)
            {
                if (d.ConnectionState == DeviceConnectionState.Online)
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

    public void OnDeviceSelected(DeviceInfo device)
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        if (device.ConnectionState == DeviceConnectionState.Online)
        {
            SelectedDevice = device;
            _pollCts = new CancellationTokenSource();
        }
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (value == null || value.Platform != DevicePlatform.Android)
        {
            StopPolling();
            MemInfoOutput = string.Empty;
            TopProcessesOutput = string.Empty;
        }
        else if (IsPolling)
        {
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

    private bool _isPollingNow;

    private async Task PollVitalsAsync()
    {
        if (_isPollingNow) return;
        if (SelectedDevice == null || SelectedDevice.Platform != DevicePlatform.Android) return;
        if (SelectedDevice.ConnectionState != DeviceConnectionState.Online)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                MemInfoOutput = $"Device is {SelectedDevice.ConnectionState}. Cannot poll vitals.";
                TopProcessesOutput = string.Empty;
            });
            return;
        }

        _isPollingNow = true;
        try
        {
            var memResult = await _adbService.ExecuteCommandAsync(SelectedDevice.Serial, "shell dumpsys meminfo");
            var topResult = await _adbService.ExecuteCommandAsync(SelectedDevice.Serial, "shell top -b -n 1");
            
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!string.IsNullOrWhiteSpace(memResult))
                {
                    var lines = memResult.Split('\n');
                    var summaryLines = lines.SkipWhile(l => !l.Contains("Total RAM")).ToList();
                    MemInfoOutput = summaryLines.Count > 0 
                        ? string.Join("\n", summaryLines).Trim() 
                        : "No memory summary available.";
                }

                if (!string.IsNullOrWhiteSpace(topResult))
                {
                    var lines = topResult.Split('\n').Take(15);
                    TopProcessesOutput = string.Join("\n", lines).Trim();
                }
            });
        }
        catch (Exception ex)
        {
            Services.AppLogger.Log.Debug(ex, "[Vitals] PollVitalsAsync temporary error");
        }
        finally
        {
            _isPollingNow = false;
        }
    }
}
