using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;
using LogPro.Services.Profiling;

namespace LogPro.ViewModels;

public partial class VitalsViewModel : ObservableObject, IDisposable
{
    private const int MaxHistory = 300;
    private const int MaxRecorded = 10000;
    private const int MaxLog = 200;
    private readonly IAdbService _adbService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly IUiDispatcher _dispatcher;
    private readonly System.Threading.Timer _pollTimer;
    private CancellationTokenSource? _pollCts;
    private int _generation;
    private int _isPollingNow;
    private int _disposed;
    private bool _isVisible;
    private bool _resumePolling = true;
    private bool _updatingDevices;
    private bool _cpuAlertActive;
    private bool _memoryAlertActive;
    private bool _batteryAlertActive;
    private DiskCounters? _lastDiskCounters;
    private DateTimeOffset? _lastDiskAt;
    private DateTimeOffset? _lastSampleAt;

    [ObservableProperty] private ObservableCollection<DeviceInfo> _devices = new();
    [ObservableProperty] private DeviceInfo? _selectedDevice;
    [ObservableProperty] private bool _isPolling;
    [ObservableProperty] private bool _isPollingSupported;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _includeIdentifiersInExport;
    [ObservableProperty] private string _capabilityMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = "Waiting for a device.";
    [ObservableProperty] private string _lastSampleText = "No sample yet";
    [ObservableProperty] private int _pollIntervalSeconds = 5;
    [ObservableProperty] private int _cpuAlertThreshold = 90;
    [ObservableProperty] private int _memoryAlertThreshold = 90;
    [ObservableProperty] private int _batteryAlertThreshold = 15;
    [ObservableProperty] private string _targetPackage = string.Empty;
    [ObservableProperty] private string _targetAppDetail = "Target app: not selected";

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private double _temperatureCelsius;
    [ObservableProperty] private double _batteryPercent;
    [ObservableProperty] private string _cpuDisplay = "--";
    [ObservableProperty] private string _cpuStatus = "Unavailable";
    [ObservableProperty] private string _memoryDisplay = "--";
    [ObservableProperty] private string _memoryStatus = "Unavailable";
    [ObservableProperty] private string _temperatureDisplay = "--";
    [ObservableProperty] private string _temperatureStatus = "Unavailable";
    [ObservableProperty] private string _batteryDisplay = "--";
    [ObservableProperty] private string _batteryStatusText = "Unavailable";
    [ObservableProperty] private string _memoryDetail = "-- / -- GB";
    [ObservableProperty] private string _batteryDetail = "STATE: --";
    [ObservableProperty] private string _thermalStatus = "Thermal severity: unavailable";
    [ObservableProperty] private string _networkSsid = "--";
    [ObservableProperty] private string _networkIp = "--";
    [ObservableProperty] private string _networkLatency = "Sample duration: unavailable";
    [ObservableProperty] private string _diskStatus = "Disk I/O: unavailable on this device";
    [ObservableProperty] private string _cpuTrend = "--";
    [ObservableProperty] private string _memoryTrend = "--";
    [ObservableProperty] private string _memInfoOutput = string.Empty;
    [ObservableProperty] private string _topProcessesOutput = string.Empty;
    [ObservableProperty] private ObservableCollection<VitalsLogEntry> _vitalsLog = new();
    [ObservableProperty] private ObservableCollection<VitalsSample> _history = new();
    public ObservableCollection<VitalsSample> RecordedSamples { get; } = new();

    public int[] PollIntervals { get; } = [3, 5, 10, 30];

    public VitalsViewModel(IAdbService adbService, IDeviceMonitorService deviceMonitor, IUiDispatcher? dispatcher = null)
    {
        _adbService = adbService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = dispatcher ?? UiServices.Dispatcher;
        _pollTimer = new System.Threading.Timer(_ =>
        {
            if (IsPolling) _ = PollVitalsAsync();
        }, null, Timeout.Infinite, Timeout.Infinite);
        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        OnDevicesChanged(_deviceMonitor.CurrentDevices.ToList());
    }

    private static bool SameDevice(DeviceInfo? a, DeviceInfo? b) =>
        a?.Serial == b?.Serial && a?.Platform == b?.Platform;

    private static bool Available(DeviceInfo? device) => device?.Platform == DevicePlatform.Android &&
        device.ConnectionState == DeviceConnectionState.Online && !device.IsTemporarilyUnavailable;

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var previous = SelectedDevice;
            var online = devices.Where(d => d.ConnectionState == DeviceConnectionState.Online).ToList();
            var next = online.FirstOrDefault(d => SameDevice(d, previous)) ?? online.FirstOrDefault();
            _updatingDevices = true;
            Devices.Clear();
            foreach (var device in online) Devices.Add(device);
            SelectedDevice = next;
            _updatingDevices = false;
            HandleDeviceChange(previous, next);
        });
    }

    public void OnDeviceSelected(DeviceInfo device)
    {
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (device.ConnectionState != DeviceConnectionState.Online)
            {
                if (SameDevice(device, SelectedDevice)) SelectedDevice = null;
                return;
            }
            var listed = Devices.FirstOrDefault(d => SameDevice(d, device)) ?? device;
            if (!ReferenceEquals(SelectedDevice, listed)) SelectedDevice = listed;
        });
    }

    partial void OnSelectedDeviceChanged(DeviceInfo? oldValue, DeviceInfo? newValue)
    {
        if (!_updatingDevices) HandleDeviceChange(oldValue, newValue);
    }

    private void HandleDeviceChange(DeviceInfo? oldValue, DeviceInfo? newValue)
    {
        IsPollingSupported = Available(newValue);
        CapabilityMessage = newValue?.Platform == DevicePlatform.iOS
            ? "Live Vitals are unavailable for iOS. Battery information is available on the Devices tab."
            : newValue == null ? "Select an available Android device to view Vitals."
            : newValue.IsTemporarilyUnavailable ? "Device reconnecting; Vitals polling is paused." : string.Empty;
        if (SameDevice(oldValue, newValue))
        {
            if (newValue?.Platform == DevicePlatform.iOS) UpdateIosBattery(newValue);
            else if (Available(oldValue) && !Available(newValue))
            {
                StopPolling(false);
                StatusMessage = "Device reconnecting; displayed values may be stale.";
            }
            else if (!Available(oldValue) && Available(newValue) && _isVisible && _resumePolling) StartPolling();
            return;
        }
        CancelCurrentSample();
        ResetMetrics();
        History.Clear();
        IsRecording = false;
        StatusMessage = newValue == null ? "Waiting for a device." : "Waiting for a fresh sample.";
        if (!IsPollingSupported)
        {
            StopPolling(false);
            if (newValue?.Platform == DevicePlatform.iOS) UpdateIosBattery(newValue);
        }
        else if (_isVisible && _resumePolling) StartPolling();
    }

    private void UpdateIosBattery(DeviceInfo device)
    {
        if (double.TryParse(device.BatteryLevel.TrimEnd('%'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var level) && level is >= 0 and <= 100)
        {
            BatteryPercent = level;
            BatteryDisplay = $"{level:F0}%";
        }
        else { BatteryPercent = 0; BatteryDisplay = "--"; }
        BatteryStatusText = BatteryDisplay == "--" ? "Unavailable" : "Discovery";
        BatteryDetail = string.IsNullOrWhiteSpace(device.BatteryStatus)
            ? "STATE: unavailable" : $"STATE: {device.BatteryStatus}";
        StatusMessage = "iOS live telemetry is unavailable; battery is from device discovery.";
    }

    partial void OnPollIntervalSecondsChanged(int value)
    {
        if (value is not (3 or 5 or 10 or 30)) PollIntervalSeconds = 5;
        else if (IsPolling) _pollTimer.Change(value * 1000, value * 1000);
    }

    partial void OnCpuAlertThresholdChanged(int value)
    {
        if (value is < 1 or > 100) CpuAlertThreshold = 90;
    }

    partial void OnMemoryAlertThresholdChanged(int value)
    {
        if (value is < 1 or > 100) MemoryAlertThreshold = 90;
    }

    partial void OnBatteryAlertThresholdChanged(int value)
    {
        if (value is < 1 or > 100) BatteryAlertThreshold = 15;
    }

    partial void OnTargetPackageChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !Regex.IsMatch(value.Trim(), @"^[A-Za-z_][A-Za-z0-9_.]{0,199}$"))
        {
            TargetAppDetail = "Target package is invalid.";
            return;
        }
        TargetAppDetail = value.Length == 0 ? "Target app: not selected" : "Waiting for target app sample.";
    }

    [RelayCommand]
    private void TogglePolling()
    {
        if (IsPolling) StopPolling(true); else { _resumePolling = true; StartPolling(); }
    }

    [RelayCommand]
    private void Record()
    {
        if (!IsPollingSupported) { StatusMessage = "Recording requires an available Android device."; return; }
        if (!IsRecording && RecordedSamples.Count >= MaxRecorded)
        {
            StatusMessage = "Recording limit reached. Export the captured samples.";
            return;
        }
        IsRecording = !IsRecording;
        AppendLog("INFO", IsRecording ? "Recording started. Up to 10,000 samples remain in memory until exported." : "Recording paused.");
    }

    [RelayCommand]
    private void ClearRecordedSamples()
    {
        if (RecordedSamples.Count == 0) return;
        if (!UiServices.Dialogs.Confirm("Clear recorded Vitals", $"Discard {RecordedSamples.Count} recorded samples?")) return;
        IsRecording = false;
        RecordedSamples.Clear();
        AppendLog("INFO", "Recorded samples cleared.");
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var samples = (RecordedSamples.Count > 0 ? RecordedSamples : History).ToArray();
        if (samples.Length == 0) { StatusMessage = "No samples to export."; return; }
        var includeIdentifiers = IncludeIdentifiersInExport;
        var fileName = $"vitals-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        var path = await UiServices.Files.SaveFileAsync("Export Vitals samples", "CSV (*.csv)|*.csv", fileName);
        if (path == null) return;
        if (!PathHelper.IsSafeLocalPath(path)) { StatusMessage = "Choose a safe local export path."; return; }
        try
        {
            await File.WriteAllTextAsync(path, BuildCsv(samples, includeIdentifiers), Encoding.UTF8);
            StatusMessage = $"Exported {samples.Length} samples to {Path.GetFileName(path)}.";
            AppendLog("INFO", StatusMessage);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Vitals] CSV export failed");
            StatusMessage = "Vitals export failed. Check the selected path and try again.";
        }
    }

    internal static string BuildCsv(IEnumerable<VitalsSample> samples, bool includeIdentifiers)
    {
        var csv = new StringBuilder("Timestamp UTC,Device,CPU %,Memory %,Memory used GB,Memory total GB,Battery %,Battery state,Battery health,Battery temperature C,Thermal severity,Target package,Target CPU %,Target PSS KB,Disk read MB/s,Disk write MB/s,Sample duration ms");
        if (includeIdentifiers) csv.Append(",SSID,IP address");
        csv.AppendLine();
        foreach (var s in samples)
        {
            csv.Append(s.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvCell(includeIdentifiers ? s.DeviceSerial : MaskSerial(s.DeviceSerial)))
                .Append(',').Append(CsvNumber(s.CpuPercent)).Append(',').Append(CsvNumber(s.MemoryPercent))
                .Append(',').Append(CsvNumber(s.MemoryUsedGb)).Append(',').Append(CsvNumber(s.MemoryTotalGb))
                .Append(',').Append(CsvNumber(s.BatteryPercent)).Append(',').Append(CsvCell(s.BatteryState))
                .Append(',').Append(CsvCell(s.BatteryHealth)).Append(',').Append(CsvNumber(s.BatteryTemperatureCelsius))
                .Append(',').Append(CsvCell(s.ThermalSeverity)).Append(',').Append(CsvCell(s.TargetPackage))
                .Append(',').Append(CsvNumber(s.TargetCpuPercent)).Append(',').Append(CsvNumber(s.TargetPssKb))
                .Append(',').Append(CsvNumber(s.DiskReadMbs)).Append(',').Append(CsvNumber(s.DiskWriteMbs))
                .Append(',').Append(CsvNumber(s.SampleDurationMs));
            if (includeIdentifiers) csv.Append(',').Append(CsvCell(s.Ssid)).Append(',').Append(CsvCell(s.IpAddress));
            csv.AppendLine();
        }
        return csv.ToString();
    }

    private static string CsvNumber(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string MaskSerial(string serial) => serial.Length <= 4 ? "****" : "****" + serial[^4..];
    private static string CsvCell(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private void StartPolling()
    {
        if (!_isVisible || !Available(SelectedDevice) || Volatile.Read(ref _disposed) != 0) return;
        CancelCurrentSample();
        _pollCts = new CancellationTokenSource();
        IsPolling = true;
        StatusMessage = "Collecting Vitals…";
        _ = PollVitalsAsync();
        _pollTimer.Change(PollIntervalSeconds * 1000, PollIntervalSeconds * 1000);
    }

    private void StopPolling(bool userPaused)
    {
        if (userPaused) _resumePolling = false;
        IsPolling = false;
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        CancelCurrentSample();
        StatusMessage = userPaused ? "Polling paused; displayed values are from the last sample."
            : "Vitals inactive; displayed values may be stale.";
        MarkCurrentStale();
    }

    private void MarkCurrentStale()
    {
        if (CpuStatus == "Current") CpuStatus = "Stale";
        if (MemoryStatus == "Current") MemoryStatus = "Stale";
        if (TemperatureStatus == "Current") TemperatureStatus = "Stale";
        if (BatteryStatusText == "Current") BatteryStatusText = "Stale";
    }

    private void CancelCurrentSample()
    {
        Interlocked.Increment(ref _generation);
        var source = Interlocked.Exchange(ref _pollCts, null);
        if (source == null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        source.Dispose();
    }

    private async Task PollVitalsAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _isPollingNow, 1) != 0)
        {
            if (_lastSampleAt is { } last && DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(PollIntervalSeconds * 2))
                _dispatcher.Post(() => { if (IsPolling) { StatusMessage = "Sampling delayed; displayed values may be stale."; MarkCurrentStale(); } });
            return;
        }
        try
        {
            var device = SelectedDevice;
            var source = _pollCts;
            if (!IsPolling || !Available(device) || source == null) return;
            var generation = Volatile.Read(ref _generation);
            CancellationToken token;
            try { token = source.Token; }
            catch (ObjectDisposedException) { return; }
            var serial = device!.Serial;
            async Task<string> ReadAsync(string command)
            {
                token.ThrowIfCancellationRequested();
                var output = await _adbService.ExecuteCommandAsync(serial, command, token);
                token.ThrowIfCancellationRequested();
                return output;
            }
            var started = System.Diagnostics.Stopwatch.StartNew();
            var mem = await ReadAsync("shell cat /proc/meminfo");
            var top = await ReadAsync("shell top -b -n 1");
            var battery = await ReadAsync("shell dumpsys battery");
            var thermal = await ReadAsync("shell dumpsys thermalservice");
            var wifi = await ReadAsync("shell dumpsys wifi | grep -E 'SSID|mWifiInfo'");
            var ip = await ReadAsync("shell ip route");
            var disk = await ReadAsync("shell cat /proc/diskstats");
            var package = TargetPackage.Trim();
            string? appCpu = null, appMem = null;
            if (package.Length > 0 && Regex.IsMatch(package, @"^[A-Za-z_][A-Za-z0-9_.]{0,199}$"))
            {
                appCpu = await ReadAsync("shell dumpsys cpuinfo");
                appMem = await ReadAsync($"shell dumpsys meminfo {package}");
            }
            started.Stop();
            var sample = VitalsParsers.Parse(DateTimeOffset.UtcNow, serial, mem, top, battery, thermal, wifi, ip,
                package, appCpu, appMem, disk, started.Elapsed.TotalMilliseconds);
            _dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) != 0 || token.IsCancellationRequested ||
                    generation != Volatile.Read(ref _generation) || !SameDevice(device, SelectedDevice) || !IsPolling) return;
                ApplySample(sample);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Log.Debug(ex, "[Vitals] Poll failed");
            _dispatcher.Post(() => { if (Volatile.Read(ref _disposed) == 0 && IsPolling) StatusMessage = "Sample failed; retrying. Previous values may be stale."; });
        }
        finally { Interlocked.Exchange(ref _isPollingNow, 0); }
    }

    private void ApplySample(VitalsSample s)
    {
        if (s.DiskCounters is { } counters)
        {
            var seconds = _lastDiskAt is { } at ? (s.TimestampUtc - at).TotalSeconds : 0;
            if (_lastDiskCounters is { } previous && seconds > 0 &&
                counters.ReadSectors >= previous.ReadSectors && counters.WriteSectors >= previous.WriteSectors)
            {
                s.DiskReadMbs = (counters.ReadSectors - previous.ReadSectors) * 512 / 1_000_000.0 / seconds;
                s.DiskWriteMbs = (counters.WriteSectors - previous.WriteSectors) * 512 / 1_000_000.0 / seconds;
                DiskStatus = $"Read {s.DiskReadMbs:F2} MB/s · Write {s.DiskWriteMbs:F2} MB/s";
            }
            else DiskStatus = "Disk I/O: waiting for a second sample";
            _lastDiskCounters = counters;
            _lastDiskAt = s.TimestampUtc;
        }
        else { _lastDiskCounters = null; _lastDiskAt = null; DiskStatus = "Disk I/O: unavailable on this device"; }
        CpuDisplay = FormatPercent(s.CpuPercent); CpuPercent = s.CpuPercent ?? 0;
        CpuStatus = s.CpuPercent is null ? "Unavailable" : "Current";
        MemoryDisplay = FormatPercent(s.MemoryPercent); MemoryPercent = s.MemoryPercent ?? 0;
        MemoryStatus = s.MemoryPercent is null ? "Unavailable" : "Current";
        MemoryDetail = s.MemoryUsedGb is { } used && s.MemoryTotalGb is { } total ? $"≈{used:F1} / {total:F1} GB" : "-- / -- GB";
        TemperatureDisplay = s.BatteryTemperatureCelsius is { } temp ? $"{temp:F1}°C (battery)" : "Unavailable";
        TemperatureStatus = s.BatteryTemperatureCelsius is null ? "Unavailable" : "Current";
        TemperatureCelsius = s.BatteryTemperatureCelsius ?? 0;
        BatteryDisplay = FormatPercent(s.BatteryPercent); BatteryPercent = s.BatteryPercent ?? 0;
        BatteryStatusText = s.BatteryPercent is null ? "Unavailable" : "Current";
        BatteryDetail = $"STATE: {s.BatteryState} · HEALTH: {s.BatteryHealth}";
        ThermalStatus = $"Thermal severity: {s.ThermalSeverity}";
        NetworkSsid = s.Ssid; NetworkIp = s.IpAddress;
        NetworkLatency = s.SampleDurationMs is { } duration ? $"Sample duration: {duration:F0} ms" : "Sample duration: unavailable";
        TargetAppDetail = s.TargetPackage.Length == 0 ? "Target app: not selected" :
            $"{s.TargetPackage}: CPU {FormatPercent(s.TargetCpuPercent)}, PSS {(s.TargetPssKb is { } kb ? $"{kb / 1024:F0} MB" : "unavailable")}";
        LastSampleText = $"Last sample: {s.TimestampUtc.ToLocalTime():HH:mm:ss}";
        _lastSampleAt = s.TimestampUtc;
        StatusMessage = s.CpuPercent == null && s.MemoryPercent == null && s.BatteryPercent == null
            ? "No supported readings returned; check ADB connection and permissions." : "Live";
        if (History.Count >= MaxHistory) History.RemoveAt(0);
        History.Add(s);
        if (IsRecording)
        {
            if (RecordedSamples.Count >= MaxRecorded) { IsRecording = false; AppendLog("INFO", "Recording limit reached. Export samples before starting again."); }
            else RecordedSamples.Add(s);
        }
        CpuTrend = Trend(History.Select(x => x.CpuPercent));
        MemoryTrend = Trend(History.Select(x => x.MemoryPercent));
        if (IsRecording) AppendLog("SAMPLE", $"CPU {CpuDisplay}, memory {MemoryDisplay}, battery {BatteryDisplay}");
        CheckAlert("CPU", s.CpuPercent >= CpuAlertThreshold, ref _cpuAlertActive);
        CheckAlert("Memory", s.MemoryPercent >= MemoryAlertThreshold, ref _memoryAlertActive);
        CheckAlert("Battery low", s.BatteryPercent <= BatteryAlertThreshold, ref _batteryAlertActive);
    }

    private static string FormatPercent(double? value) => value is { } n ? $"{n:F0}%" : "--";
    private static string Trend(IEnumerable<double?> values)
    {
        const string blocks = "▁▂▃▄▅▆▇█";
        var recent = values.TakeLast(24).ToArray();
        return recent.Length == 0 ? "--" : new string(recent.Select(v => v is { } n ? blocks[Math.Clamp((int)(n / 100 * 7), 0, 7)] : '·').ToArray());
    }

    private void CheckAlert(string name, bool active, ref bool wasActive)
    {
        if (active && !wasActive) AppendLog("ALERT", $"{name} threshold reached.");
        wasActive = active;
    }

    private void ResetMetrics()
    {
        CpuPercent = MemoryPercent = TemperatureCelsius = BatteryPercent = 0;
        CpuDisplay = MemoryDisplay = BatteryDisplay = "--";
        CpuStatus = MemoryStatus = TemperatureStatus = BatteryStatusText = "Unavailable";
        TemperatureDisplay = "--"; MemoryDetail = "-- / -- GB"; BatteryDetail = "STATE: --";
        ThermalStatus = "Thermal severity: unavailable";
        NetworkSsid = NetworkIp = "--"; NetworkLatency = "Sample duration: unavailable";
        DiskStatus = "Disk I/O: unavailable on this device";
        _lastDiskCounters = null; _lastDiskAt = null;
        TargetAppDetail = "Target app: not selected";
        CpuTrend = MemoryTrend = "--";
        LastSampleText = "No sample yet";
        _lastSampleAt = null;
        MemInfoOutput = TopProcessesOutput = string.Empty;
        _cpuAlertActive = _memoryAlertActive = _batteryAlertActive = false;
    }

    private void AppendLog(string level, string message)
    {
        if (VitalsLog.Count >= MaxLog) VitalsLog.RemoveAt(0);
        VitalsLog.Add(new VitalsLogEntry { Timestamp = DateTime.Now.ToString("HH:mm:ss"), Level = level, Message = message });
    }

    public void OnNavigatedFrom()
    {
        _isVisible = false;
        StopPolling(false);
        if (IsRecording) { IsRecording = false; AppendLog("INFO", "Recording paused when leaving Vitals."); }
    }
    public void OnNavigatedTo() { _isVisible = true; if (_resumePolling) StartPolling(); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _pollTimer.Dispose();
        CancelCurrentSample();
        GC.SuppressFinalize(this);
    }
}

public sealed class VitalsSample
{
    public DateTimeOffset TimestampUtc { get; init; }
    public string DeviceSerial { get; init; } = "";
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? MemoryUsedGb { get; init; }
    public double? MemoryTotalGb { get; init; }
    public double? BatteryPercent { get; init; }
    public string BatteryState { get; init; } = "Unavailable";
    public string BatteryHealth { get; init; } = "Unavailable";
    public double? BatteryTemperatureCelsius { get; init; }
    public string ThermalSeverity { get; init; } = "Unavailable";
    public string Ssid { get; init; } = "--";
    public string IpAddress { get; init; } = "--";
    public string TargetPackage { get; init; } = "";
    public double? TargetCpuPercent { get; init; }
    public double? TargetPssKb { get; init; }
    public double? SampleDurationMs { get; init; }
    internal DiskCounters? DiskCounters { get; init; }
    public double? DiskReadMbs { get; set; }
    public double? DiskWriteMbs { get; set; }
}

internal readonly record struct DiskCounters(long ReadSectors, long WriteSectors);

public sealed class VitalsLogEntry
{
    public string Timestamp { get; set; } = "";
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
}

internal static class VitalsParsers
{
    public static VitalsSample Parse(DateTimeOffset time, string serial, string mem, string top, string battery,
        string thermal, string wifi, string ip, string package, string? appCpu, string? appMem, string disk, double rttMs)
    {
        double? total = Number(mem, @"(?m)^MemTotal:\s*([\d,]+)\s*kB") ?? Number(mem, @"Total RAM:\s*([\d,]+)K");
        double? free = Number(mem, @"(?m)^MemAvailable:\s*([\d,]+)\s*kB") ?? Number(mem, @"Free RAM:\s*([\d,]+)K");
        var memoryValid = total > 0 && free >= 0 && free <= total;
        var cpu = ParseCpu(top);
        var level = AndroidDumpsysParsers.ParseBatteryLevel(battery);
        var status = Number(battery, @"(?m)^\s*status:\s*(\d+)");
        var health = Number(battery, @"(?m)^\s*health:\s*(\d+)");
        var temperature = Number(battery, @"(?m)^\s*temperature:\s*(-?\d+)");
        var severity = AndroidDumpsysParsers.ParseThermalStatus(thermal);
        var ssid = Regex.Match(wifi, "SSID:\\s*\"?([^\"\\n,]+)", RegexOptions.IgnoreCase);
        var address = Regex.Match(ip, @"\bsrc\s+(\d+\.\d+\.\d+\.\d+)");
        var appMemory = appMem == null ? (int?)null : AndroidDumpsysParsers.ParseMemInfoTotals(appMem).PssKb;
        return new VitalsSample
        {
            TimestampUtc = time,
            DeviceSerial = serial,
            CpuPercent = cpu,
            MemoryPercent = memoryValid ? Math.Round((total!.Value - free!.Value) / total.Value * 100, 1) : null,
            MemoryUsedGb = memoryValid ? (total!.Value - free!.Value) / 1048576 : null,
            MemoryTotalGb = memoryValid ? total!.Value / 1048576 : null,
            BatteryPercent = level is >= 0 and <= 100 ? level : null,
            BatteryState = status switch { 2 => "Charging", 3 => "Discharging", 4 => "Not charging", 5 => "Full", _ => "Unavailable" },
            BatteryHealth = health switch { 2 => "Good", 3 => "Overheat", 4 => "Dead", 5 => "Overvoltage", 6 => "Failure", 7 => "Cold", _ => "Unavailable" },
            BatteryTemperatureCelsius = temperature is >= -400 and <= 1200 ? temperature / 10 : null,
            ThermalSeverity = severity switch { 0 => "None", 1 => "Light", 2 => "Moderate", 3 => "Severe", 4 => "Critical", 5 => "Emergency", 6 => "Shutdown", _ => "Unavailable" },
            Ssid = ssid.Success ? ssid.Groups[1].Value.Trim() : "--",
            IpAddress = address.Success ? address.Groups[1].Value : "--",
            TargetPackage = package,
            TargetCpuPercent = appCpu == null || package.Length == 0 ? null : AndroidDumpsysParsers.ParseCpuPercent(appCpu, package),
            TargetPssKb = appMemory,
            DiskCounters = ParseDiskCounters(disk),
            SampleDurationMs = rttMs >= 0 ? rttMs : null
        };
    }

    internal static DiskCounters? ParseDiskCounters(string output)
    {
        long reads = 0, writes = 0;
        var found = false;
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 || !Regex.IsMatch(fields[2], @"^(mmcblk\d+|sd[a-z]+|nvme\d+n\d+)$")) continue;
            if (!long.TryParse(fields[5], out var read) || !long.TryParse(fields[9], out var write) || read < 0 || write < 0) continue;
            reads += read; writes += write; found = true;
        }
        return found ? new DiskCounters(reads, writes) : null;
    }

    internal static double? ParseCpu(string top)
    {
        // Toybox top reports a per-core capacity (e.g. 400%cpu for four cores).
        // Actual device usage is user + system (+ nice/irq when present) divided by capacity.
        var capacity = Number(top, @"(?i)(\d+(?:\.\d+)?)%cpu");
        var idle = Number(top, @"(?i)(\d+(?:\.\d+)?)%idle");
        if (capacity > 0 && idle is >= 0 && idle <= capacity)
            return Math.Round(Math.Clamp((capacity.Value - idle.Value) / capacity.Value * 100, 0, 100), 1);
        var user = Number(top, @"(?i)(\d+(?:\.\d+)?)%user");
        var system = Number(top, @"(?i)(\d+(?:\.\d+)?)%sys");
        if (capacity > 0 && user >= 0 && system >= 0)
            return Math.Round(Math.Clamp((user!.Value + system!.Value) / capacity.Value * 100, 0, 100), 1);
        var legacy = Regex.Match(top, @"(?i)User\s+(\d+(?:\.\d+)?)%.*Sys\s+(\d+(?:\.\d+)?)%");
        if (legacy.Success && double.TryParse(legacy.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var u) &&
            double.TryParse(legacy.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return Math.Clamp(u + s, 0, 100);
        return null;
    }

    private static double? Number(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success && double.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
