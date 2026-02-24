using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Data;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;

namespace QADeviceTool.ViewModels;

/// <summary>
/// Sessions view — one-click capture, live log viewer with auto-scroll,
/// session-scoped snapshots, save logs, auto-capture on connect.
/// </summary>
public partial class SessionViewModel : ObservableObject
{
    private readonly SessionService _sessionService;
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly Dispatcher _dispatcher;

    // ── Log Viewer Properties ──
    public ObservableCollection<LogEntry> LogEntries { get; } = new();
    public ICollectionView LogEntriesView { get; }
    
    // UI scroll scroll-to-end event
    public event Action? ScrollToEndRequested;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LogLevel _selectedLogLevel = LogLevel.Verbose;

    public Array LogLevels => Enum.GetValues(typeof(LogLevel));

    [ObservableProperty]
    private ObservableCollection<LogSession> _sessions = new();

    [ObservableProperty]
    private LogSession? _selectedSession;

    [ObservableProperty]
    private string _newSessionName = string.Empty;

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _availableDevices = new();

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _autoCapture = true;

    public SessionViewModel(SessionService sessionService, AdbService adbService, IosService iosService, DeviceMonitorService deviceMonitor)
    {
        _sessionService = sessionService;
        _adbService = adbService;
        _iosService = iosService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = Application.Current.Dispatcher;

        LogEntriesView = CollectionViewSource.GetDefaultView(LogEntries);
        LogEntriesView.Filter = FilterLogEntry;

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        _deviceMonitor.DeviceConnected += OnDeviceConnected;
        _deviceMonitor.DeviceDisconnected += OnDeviceDisconnected;

        // Populate device list from current state (devices may already be connected)
        var currentDevices = _deviceMonitor.CurrentDevices;
        foreach (var d in currentDevices)
            AvailableDevices.Add(d);
        if (currentDevices.Count > 0)
            SelectedDevice = currentDevices[0];

        try { LoadSessions(); } catch { }
    }

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.BeginInvoke(() =>
        {
            AvailableDevices.Clear();
            foreach (var d in devices)
                AvailableDevices.Add(d);

            // If the previously selected device was unplugged, clear it
            if (SelectedDevice != null && !devices.Any(d => d.Serial == SelectedDevice.Serial))
                SelectedDevice = null;

            // Auto-select the first available device
            if (SelectedDevice == null && devices.Count > 0)
                SelectedDevice = devices[0];
        });
    }

    /// <summary>
    /// Auto-start a new logging session when a device is plugged in.
    /// </summary>
    private void OnDeviceConnected(DeviceInfo device)
    {
        if (!AutoCapture) return;

        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Don't start a second capture if one is already active for this device
                var alreadyActive = Sessions.Any(s =>
                    s.DeviceId == device.Serial && s.Status == SessionStatus.Capturing);
                if (alreadyActive) return;

                SelectedDevice = device;

                var session = _sessionService.CreateSession(device);
                Sessions.Insert(0, session);
                SelectedSession = session;

                var started = _sessionService.StartCapture(session);
                if (started)
                {
                    IsCapturing = true;
                    LogEntries.Clear();
                    AddLogEntry($"[{DateTime.Now:HH:mm:ss}] Device connected - auto-capture started for {device.DisplayName} ({device.Serial})", LogLevel.Info);
                    StatusMessage = $"[REC] Auto-capturing - {device.DisplayName} ({device.Serial})";
                    _sessionService.LogBatchReceived += OnLogBatchReceived;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"[!] Auto-capture error: {ex.Message}";
            }
        });
    }

    /// <summary>
    /// Immediately stop logging when a device is unplugged.
    /// </summary>
    private void OnDeviceDisconnected(DeviceInfo device)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                _sessionService.LogBatchReceived -= OnLogBatchReceived;

                var stoppedSession = _sessionService.StopCaptureForDevice(device.Serial, Sessions);
                if (stoppedSession != null)
                {
                    IsCapturing = false;
                    AddLogEntry($"[{DateTime.Now:HH:mm:ss}] [!] Device disconnected - capture stopped.", LogLevel.Warning);
                    StatusMessage = $"[STOP] Device disconnected. {stoppedSession.LogLineCount} lines captured > {System.IO.Path.GetFileName(stoppedSession.LogFilePath)}";
                    OnPropertyChanged(nameof(SelectedSession));
                }
            }
            catch { }
        });
    }

    private void LoadSessions()
    {
        try
        {
            var saved = _sessionService.GetSavedSessions();
            Sessions.Clear();
            foreach (var s in saved)
                Sessions.Add(s);
        }
        catch { }
    }

    [RelayCommand]
    private void StartCapture()
    {
        try
        {
            var device = SelectedDevice;
            if (device == null)
            {
                if (AvailableDevices.Count > 0)
                {
                    device = AvailableDevices[0];
                    SelectedDevice = device;
                }
                else
                {
                    StatusMessage = "[!] No devices connected. Plug in a device via USB.";
                    return;
                }
            }

            if (SelectedSession == null || SelectedSession.Status != SessionStatus.Idle)
            {
                var session = _sessionService.CreateSession(device);
                Sessions.Insert(0, session);
                SelectedSession = session;
                NewSessionName = string.Empty;
            }

            var started = _sessionService.StartCapture(SelectedSession!);
            if (started)
            {
                IsCapturing = true;
                LogEntries.Clear();
                AddLogEntry($"[{DateTime.Now:HH:mm:ss}] Capture started for {device.DisplayName} ({device.Serial})...", LogLevel.Info);
                StatusMessage = $"[REC] Capturing - {device.DisplayName} ({device.Serial})";

                _sessionService.LogBatchReceived += OnLogBatchReceived;
            }
            else
            {
                StatusMessage = "[!] Failed to start capture. Check if ADB/iOS tools are available.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Error: {ex.Message}";
        }
    }

    private void OnLogBatchReceived(string batch)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var lines = batch.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                ParseAndAddLogEntry(line);
            }

            // Keep memory in check (max ~50k rows in UI)
            if (LogEntries.Count > 50000)
            {
                for (int i = 0; i < 10000; i++) LogEntries.RemoveAt(0);
            }

            ScrollToEndRequested?.Invoke();
        });
    }

    private void AddLogEntry(string message, LogLevel level)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            LogEntries.Add(new LogEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Level = level,
                Message = message,
                RawLine = message
            });
            ScrollToEndRequested?.Invoke();
        });
    }

    private void ParseAndAddLogEntry(string rawLine)
    {
        var entry = new LogEntry { RawLine = rawLine, Level = LogLevel.Unknown };
        try
        {
            if (rawLine.StartsWith("["))
            {
                int closeBracket = rawLine.IndexOf(']');
                if (closeBracket > 1)
                {
                    entry.Timestamp = rawLine.Substring(1, closeBracket - 1);
                    var rest = rawLine.Substring(closeBracket + 1).TrimStart();
                    entry.Message = rest;

                    if (rest.Contains(" E ") || rest.Contains(" F ") || rest.StartsWith("E/") || rest.StartsWith("F/"))
                        entry.Level = LogLevel.Error;
                    else if (rest.Contains(" W ") || rest.StartsWith("W/"))
                        entry.Level = LogLevel.Warning;
                    else if (rest.Contains(" D ") || rest.StartsWith("D/"))
                        entry.Level = LogLevel.Debug;
                    else if (rest.Contains(" I ") || rest.StartsWith("I/"))
                        entry.Level = LogLevel.Info;
                    else if (rest.Contains(" V ") || rest.StartsWith("V/"))
                        entry.Level = LogLevel.Verbose;
                }
                else entry.Message = rawLine;
            }
            else entry.Message = rawLine;
        }
        catch { entry.Message = rawLine; }

        LogEntries.Add(entry);
    }

    [RelayCommand]
    private void StopCapture()
    {
        try
        {
            if (SelectedSession == null) return;

            _sessionService.LogBatchReceived -= OnLogBatchReceived;
            _sessionService.StopCapture(SelectedSession);
            IsCapturing = false;
            StatusMessage = $"[STOP] Stopped. {SelectedSession.LogLineCount} lines captured > {Path.GetFileName(SelectedSession.LogFilePath)}";
            OnPropertyChanged(nameof(SelectedSession));
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Error stopping: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        try
        {
            if (SelectedSession == null)
            {
                StatusMessage = "[!] No active session to save.";
                return;
            }

            // The log is already being saved to disk in real-time by SessionService
            StatusMessage = $"Log is saved at: {SelectedSession.LogFilePath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Save error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TakeSnapshotAsync()
    {
        try
        {
            var device = SelectedDevice ?? (AvailableDevices.Count > 0 ? AvailableDevices[0] : null);
            if (device == null)
            {
                StatusMessage = "[!] No device connected for snapshot.";
                return;
            }

            string saveDir;
            if (SelectedSession != null && !string.IsNullOrEmpty(SelectedSession.SessionDirectory))
            {
                saveDir = SelectedSession.SessionDirectory;
                if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
            }
            else
            {
                saveDir = Helpers.PathHelper.GetDefaultSessionsDirectory();
            }

            var fileName = $"snapshot_{device.Serial}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var outputPath = Path.Combine(saveDir, fileName);

            StatusMessage = "Capturing snapshot...";

            bool success = device.Platform == DevicePlatform.Android
                ? await _adbService.CaptureScreenshotAsync(device.Serial, outputPath)
                : await _iosService.CaptureScreenshotAsync(device.Serial, outputPath);

            if (success)
            {
                StatusMessage = $"Snapshot saved: {fileName}";
                AddLogEntry($"Snapshot saved: {fileName}", LogLevel.Info);
            }
            else
            {
                StatusMessage = "[!] Snapshot failed. Check device connection.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Snapshot error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task GenerateBugReportAsync()
    {
        try
        {
            var device = SelectedDevice ?? (AvailableDevices.Count > 0 ? AvailableDevices[0] : null);
            if (device == null)
            {
                StatusMessage = "[!] No device connected for bug report.";
                return;
            }

            string saveDir = SelectedSession != null && !string.IsNullOrEmpty(SelectedSession.SessionDirectory)
                ? SelectedSession.SessionDirectory
                : Helpers.PathHelper.GetDefaultSessionsDirectory();

            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);

            StatusMessage = "Generating Bug Report...";
            
            // 1. Snapshot
            var snapshotName = $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var snapshotPath = Path.Combine(saveDir, snapshotName);
            bool snapSuccess = device.Platform == DevicePlatform.Android
                ? await _adbService.CaptureScreenshotAsync(device.Serial, snapshotPath)
                : await _iosService.CaptureScreenshotAsync(device.Serial, snapshotPath);

            // 2. Dump Logs (last 10k lines)
            var logDumpName = $"log_dump_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var logDumpPath = Path.Combine(saveDir, logDumpName);
            var logLines = LogEntries.TakeLast(10000).Select(e => e.RawLine).ToList();
            await File.WriteAllLinesAsync(logDumpPath, logLines);

            // 3. Device Info / Dumpsys
            var infoName = $"device_info_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var infoPath = Path.Combine(saveDir, infoName);
            if (device.Platform == DevicePlatform.Android)
            {
                var memInfo = await _adbService.ExecuteCommandAsync(device.Serial, "shell dumpsys meminfo");
                await File.WriteAllTextAsync(infoPath, $"Device: {device.DisplayName}\nSerial: {device.Serial}\n\n=== MEMINFO ===\n{memInfo}");
            }
            else
            {
                await File.WriteAllTextAsync(infoPath, $"Device: {device.DisplayName}\nSerial: {device.Serial}\nPlatform: iOS");
            }

            // 4. Zip them
            var zipName = $"BugReport_{device.Serial}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var zipPath = Path.Combine(saveDir, zipName);
            
            using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                if (File.Exists(snapshotPath)) archive.CreateEntryFromFile(snapshotPath, snapshotName);
                if (File.Exists(logDumpPath)) archive.CreateEntryFromFile(logDumpPath, logDumpName);
                if (File.Exists(infoPath)) archive.CreateEntryFromFile(infoPath, infoName);
            }

            // Clean up raw files
            if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
            if (File.Exists(logDumpPath)) File.Delete(logDumpPath);
            if (File.Exists(infoPath)) File.Delete(infoPath);

            StatusMessage = $"Bug Report Zip saved: {zipName}";
            AddLogEntry($"Bug report generated: {zipPath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Bug Report error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CreateSession()
    {
        try
        {
            var device = SelectedDevice ?? (AvailableDevices.Count > 0 ? AvailableDevices[0] : null);
            if (device == null)
            {
                StatusMessage = "[!] No device connected.";
                return;
            }

            var session = _sessionService.CreateSession(device);
            Sessions.Insert(0, session);
            SelectedSession = session;
            NewSessionName = string.Empty;
            StatusMessage = $"Session '{session.Name}' created.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteSession()
    {
        try
        {
            if (SelectedSession == null) return;
            _sessionService.StopCapture(SelectedSession);
            _sessionService.DeleteSession(SelectedSession);
            Sessions.Remove(SelectedSession);
            SelectedSession = null;
            IsCapturing = false;
            LogEntries.Clear();
            StatusMessage = "Session deleted.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenSessionFolder()
    {
        try
        {
            if (SelectedSession == null) return;
            var dir = SelectedSession.SessionDirectory;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
        }
        catch { }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
    }

    private bool FilterLogEntry(object obj)
    {
        if (obj is not LogEntry entry) return false;

        if (SelectedLogLevel != LogLevel.Verbose)
        {
            if (entry.Level < SelectedLogLevel && entry.Level != LogLevel.Unknown)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            return entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   entry.Tag.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    partial void OnSearchTextChanged(string value)
    {
        LogEntriesView.Refresh();
    }

    partial void OnSelectedLogLevelChanged(LogLevel value)
    {
        LogEntriesView.Refresh();
    }

    partial void OnSelectedSessionChanged(LogSession? value)
    {
        if (value != null)
        {
            _ = LoadSessionLogSafe(value);
        }
        else
        {
            LogEntries.Clear();
            AddLogEntry("Connect a device and click 'Start Capture' to begin.", LogLevel.Info);
        }
    }

    private async Task LoadSessionLogSafe(LogSession session)
    {
        try
        {
            if (string.IsNullOrEmpty(session.LogFilePath) || !File.Exists(session.LogFilePath))
            {
                LogEntries.Clear();
                AddLogEntry(session.Status == SessionStatus.Idle
                    ? "Ready to capture. Click 'Start' to begin."
                    : "No log file found.", LogLevel.Info);
                return;
            }

            LogEntries.Clear();
            AddLogEntry("Loading log...", LogLevel.Info);
            var content = await _sessionService.ReadLogContentAsync(session);
            OnLogBatchReceived(content);
        }
        catch
        {
            AddLogEntry("Could not load log file.", LogLevel.Error);
        }
    }
}
