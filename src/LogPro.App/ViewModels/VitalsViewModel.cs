using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using LogPro.Services;
using LogPro.Helpers;

namespace LogPro.ViewModels;

public partial class VitalsViewModel : ObservableObject, IDisposable
{
    private readonly IAdbService _adbService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly IUiDispatcher _dispatcher;
    private DispatcherTimer? _pollTimer;
    private CancellationTokenSource? _pollCts;

    [ObservableProperty] private ObservableCollection<DeviceInfo> _devices = new();
    [ObservableProperty] private DeviceInfo? _selectedDevice;
    [ObservableProperty] private string _memInfoOutput = "Select a device and start polling.";
    [ObservableProperty] private string _topProcessesOutput = string.Empty;
    [ObservableProperty] private bool _isPolling;

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _memoryDetail = "-- / -- GB";
    [ObservableProperty] private double _temperatureCelsius;
    [ObservableProperty] private double _batteryPercent;
    [ObservableProperty] private string _batteryDetail = "STATE: --";
    [ObservableProperty] private string _networkSsid = "--";
    [ObservableProperty] private string _networkIp = "--";
    [ObservableProperty] private string _networkLatency = "-- ms";
    [ObservableProperty] private double _diskReadMbs;
    [ObservableProperty] private double _diskWriteMbs;
    [ObservableProperty] private ObservableCollection<VitalsLogEntry> _vitalsLog = new();
    [ObservableProperty] private bool _autoScroll = true;

    public VitalsViewModel(IAdbService adbService, IDeviceMonitorService deviceMonitor)
    {
        _adbService = adbService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = new WpfUiDispatcher(Application.Current.Dispatcher);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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
        _dispatcher.Post(() =>
        {
            var currentSelected = SelectedDevice?.Serial;
            Devices.Clear();
            foreach (var d in devices)
                if (d.ConnectionState == DeviceConnectionState.Online) Devices.Add(d);

            if (!string.IsNullOrEmpty(currentSelected))
                SelectedDevice = Devices.FirstOrDefault(d => d.Serial == currentSelected);
            if (SelectedDevice == null && Devices.Count > 0)
                SelectedDevice = Devices.First();
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
        else if (IsPolling) _ = PollVitalsAsync();
    }

    [RelayCommand]
    private void TogglePolling()
    {
        if (IsPolling) StopPolling(); else StartPolling();
    }

    [RelayCommand]
    private void Record()
    {
        AppendLog("Sys_Info", "Recording toggled.");
    }

    [RelayCommand]
    private void ExportCsv()
    {
        AppendLog("Sys_Info", "CSV export requested.");
    }

    private void StartPolling()
    {
        if (SelectedDevice == null) return;
        IsPolling = true;
        _ = PollVitalsAsync();
        _pollTimer?.Start();
        AppendLog("Sys_Info", $"Vitals polling started for {SelectedDevice.DisplayName}.");
    }

    private void StopPolling()
    {
        IsPolling = false;
        _pollTimer?.Stop();
    }

    private bool _isPollingNow;

    private async Task PollVitalsAsync()
    {
        if (_isPollingNow) return;
        if (SelectedDevice == null || SelectedDevice.Platform != DevicePlatform.Android) return;
        if (SelectedDevice.ConnectionState != DeviceConnectionState.Online)
        {
            _dispatcher.Post(() =>
            {
                MemInfoOutput = $"Device is {SelectedDevice.ConnectionState}. Cannot poll vitals.";
                TopProcessesOutput = string.Empty;
            });
            return;
        }

        _isPollingNow = true;
        try
        {
            var serial = SelectedDevice.Serial;
            var memResult = await _adbService.ExecuteCommandAsync(serial, "shell dumpsys meminfo");
            var topResult = await _adbService.ExecuteCommandAsync(serial, "shell top -b -n 1");
            var batteryResult = await _adbService.ExecuteCommandAsync(serial, "shell dumpsys battery");
            var thermalResult = await _adbService.ExecuteCommandAsync(serial, "shell cat /sys/class/thermal/thermal_zone0/temp");
            var wifiResult = await _adbService.ExecuteCommandAsync(serial, "shell dumpsys wifi | grep -E 'SSID|mWifiInfo'");
            var ipResult = await _adbService.ExecuteCommandAsync(serial, "shell ip route");

            _dispatcher.Post(() =>
            {
                ParseMemory(memResult);
                ParseCpu(topResult);
                ParseBattery(batteryResult);
                ParseTemperature(thermalResult);
                ParseNetwork(wifiResult, ipResult);
            });
        }
        catch (Exception ex)
        {
            Services.AppLogger.Log.Debug(ex, "[Vitals] PollVitalsAsync temporary error");
        }
        finally { _isPollingNow = false; }
    }

    private void ParseMemory(string? memResult)
    {
        if (string.IsNullOrWhiteSpace(memResult)) return;
        var lines = memResult.Split('\n');
        var summary = lines.SkipWhile(l => !l.Contains("Total RAM")).ToList();
        MemInfoOutput = summary.Count > 0 ? string.Join("\n", summary).Trim() : memResult.Trim();

        var totalMatch = Regex.Match(memResult, @"Total RAM:\s*([\d,]+)K");
        var freeMatch = Regex.Match(memResult, @"Free RAM:\s*([\d,]+)K");
        if (totalMatch.Success && freeMatch.Success)
        {
            if (long.TryParse(totalMatch.Groups[1].Value.Replace(",", ""), out var totalKb) &&
                long.TryParse(freeMatch.Groups[1].Value.Replace(",", ""), out var freeKb) && totalKb > 0)
            {
                double totalGb = totalKb / 1024.0 / 1024.0;
                double usedGb = (totalKb - freeKb) / 1024.0 / 1024.0;
                MemoryPercent = Math.Round((double)(totalKb - freeKb) / totalKb * 100, 1);
                MemoryDetail = string.Format(CultureInfo.InvariantCulture, "{0:F1} / {1:F1} GB", usedGb, totalGb);
            }
        }
    }

    private void ParseCpu(string? topResult)
    {
        if (string.IsNullOrWhiteSpace(topResult)) return;
        var lines = topResult.Split('\n').Take(15);
        TopProcessesOutput = string.Join("\n", lines).Trim();

        var m = Regex.Match(topResult, @"(\d+)%cpu", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(topResult, @"User\s+(\d+)%.*Sys\s+(\d+)%", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var cpu))
            CpuPercent = Math.Clamp(cpu, 0, 100);
    }

    private void ParseBattery(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return;
        var lvl = Regex.Match(result, @"level:\s*(\d+)");
        var status = Regex.Match(result, @"status:\s*(\d+)");
        var health = Regex.Match(result, @"health:\s*(\d+)");
        if (lvl.Success && int.TryParse(lvl.Groups[1].Value, out var pct))
            BatteryPercent = Math.Clamp(pct, 0, 100);

        string charging = status.Success && status.Groups[1].Value == "2" ? "YES" : "NO";
        string healthStr = health.Success ? health.Groups[1].Value : "--";
        BatteryDetail = $"CHARGING: {charging} · HEALTH: {healthStr}";
    }

    private void ParseTemperature(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return;
        var raw = result.Trim().Split('\n').FirstOrDefault()?.Trim();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            TemperatureCelsius = v > 1000 ? Math.Round(v / 1000.0, 1) : Math.Round(v, 1);
    }

    private void ParseNetwork(string? wifi, string? ip)
    {
        if (!string.IsNullOrWhiteSpace(wifi))
        {
            var ssid = Regex.Match(wifi, @"SSID:\s*""?([^""\n,]+)""?");
            if (ssid.Success) NetworkSsid = ssid.Groups[1].Value.Trim();
        }
        if (!string.IsNullOrWhiteSpace(ip))
        {
            var m = Regex.Match(ip, @"src\s+(\d+\.\d+\.\d+\.\d+)");
            if (m.Success) NetworkIp = m.Groups[1].Value;
        }
    }

    private void AppendLog(string level, string message)
    {
        var entry = new VitalsLogEntry
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Level = level,
            Message = message
        };
        if (VitalsLog.Count > 200) VitalsLog.RemoveAt(0);
        VitalsLog.Add(entry);
    }

    public void OnNavigatedFrom() => StopPolling();
    public void OnNavigatedTo()
    {
        if (SelectedDevice != null && SelectedDevice.Platform == DevicePlatform.Android)
            StartPolling();
    }

    public void Dispose()
    {
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _pollTimer?.Stop(); _pollTimer = null;
        _pollCts?.Cancel(); _pollCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class VitalsLogEntry
{
    public string Timestamp { get; set; } = "";
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
}
