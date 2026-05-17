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
using LogPro.Models;
using LogPro.Services;

namespace LogPro.ViewModels;

/// <summary>
/// Sessions view — one-click capture, live log viewer with auto-scroll,
/// session-scoped snapshots, save logs, auto-capture on connect.
/// </summary>
public partial class SessionViewModel : ObservableObject, IDisposable
{
    private readonly ISessionService _sessionService;
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly IDeviceMonitorService _deviceMonitor;
    private readonly Dispatcher _dispatcher;

    // ── Log Viewer Properties ──
    public BulkObservableCollection<LogEntry> LogEntries { get; } = new();
    public ICollectionView LogEntriesView { get; }
    
    // UI scroll scroll-to-end event
    public event Action? ScrollToEndRequested;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LogLevel _selectedLogLevel = LogLevel.Verbose;

    public Array LogLevels => Enum.GetValues(typeof(LogLevel));

    [ObservableProperty]
    private ObservableCollection<LogLevelFilterItem> _logLevelFilters = new();

    [ObservableProperty]
    private ObservableCollection<LogSession> _sessions = new();

    [ObservableProperty]
    private LogSession? _selectedSession;

    [ObservableProperty]
    private string _newSessionName = string.Empty;

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private bool _anonymizeExport = false;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _availableDevices = new();

    [ObservableProperty]
    private bool _isCapturing;

    private bool _isSubscribedToLogBatch;
    private bool _isLoadingSession;
    private readonly CrashDetector _crashDetector = new();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _crashCount;

    [ObservableProperty]
    private bool _hasCrashAlert;

    // Screen recording
    [ObservableProperty]
    private bool _isScreenRecording;

    [ObservableProperty]
    private string _screenRecordStatus = string.Empty;

    private string? _screenRecordRemotePath;

    [ObservableProperty]
    private bool _autoCapture;

    [ObservableProperty]
    private bool _isColorCodingEnabled = true;

    [ObservableProperty]
    private bool _isRawMode = true;

    [ObservableProperty]
    private bool _isAutoScrollEnabled = true;

    [ObservableProperty]
    private LogcatBuffer _selectedLogBuffer = LogcatBuffer.Main;

    [ObservableProperty]
    private LogcatFormat _selectedLogFormat = LogcatFormat.ThreadTime;

    public SessionViewModel(ISessionService sessionService, IAdbService adbService, IIosService iosService, DeviceMonitorService deviceMonitor)
    {
        _sessionService = sessionService;
        _adbService = adbService;
        _iosService = iosService;
        _deviceMonitor = deviceMonitor;
        _dispatcher = Application.Current.Dispatcher;

        LogEntriesView = CollectionViewSource.GetDefaultView(LogEntries);
        LogEntriesView.Filter = FilterLogEntry;

        InitializeLogLevelFilters();

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        _deviceMonitor.DeviceConnected += OnDeviceConnected;
        _deviceMonitor.DeviceDisconnected += OnDeviceDisconnected;

        // Populate device list from current state (devices may already be connected)
        var currentDevices = _deviceMonitor.CurrentDevices;
        foreach (var d in currentDevices)
            AvailableDevices.Add(d);
        if (currentDevices.Count > 0)
            SelectedDevice = currentDevices[0];

        try { LoadSessions(); } catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionVM] LoadSessions failed"); }

        _crashDetector.CrashDetected += OnCrashDetected;
    }

    private void OnCrashDetected(CrashDetector.CrashEvent crash)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            CrashCount = _crashDetector.CrashCount;
            HasCrashAlert = true;
            StatusMessage = $"[CRASH DETECTED] {crash.Platform} — line #{crash.LineIndex}";
        });
    }

    private void InitializeLogLevelFilters()
    {
        LogLevelFilters.Clear();
        LogLevelFilters.Add(new LogLevelFilterItem(LogLevel.Fatal, true));
        LogLevelFilters.Add(new LogLevelFilterItem(LogLevel.Error, true));
        LogLevelFilters.Add(new LogLevelFilterItem(LogLevel.Warning, true));
        LogLevelFilters.Add(new LogLevelFilterItem(LogLevel.Info, true));
        LogLevelFilters.Add(new LogLevelFilterItem(LogLevel.Debug, true));
        LogLevelFilters.Add(new LogLevelFilterItem(LogLevel.Verbose, true));
        LogLevelFilters.Add(new LogLevelFilterItem(LogLevel.Unknown, true));

        foreach (var filter in LogLevelFilters)
        {
            filter.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LogLevelFilterItem.IsSelected))
                {
                    LogEntriesView.Refresh();
                }
            };
        }
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

    public void OnDeviceSelected(DeviceInfo device)
    {
        SelectedDevice = device;
    }

    /// <summary>
    /// Auto-start a new logging session when a device is plugged in.
    /// </summary>
    private void OnDeviceConnected(DeviceInfo device)
    {
        if (!AutoCapture) return;

        _dispatcher.BeginInvoke(async () =>
        {
            // Prevent re-entrant auto-capture from rapid DeviceConnected events
            lock (_autoCaptureLock)
            {
                if (!_autoCaptureInProgress.Add(device.Serial)) return;
            }
            try
            {
                // Don't start a second capture if one is already active for this device
                var alreadyActive = Sessions.Any(s =>
                    s.DeviceSerial == device.Serial && s.Status == SessionStatus.Capturing);
                if (alreadyActive) return;

                SelectedDevice = device;

                var session = _sessionService.CreateSession(device, NewSessionName);
                Sessions.Insert(0, session);
                SelectedSession = session;

                var started = await _sessionService.StartCaptureAsync(session, SelectedLogBuffer, SelectedLogFormat);
                if (started)
                {
                    IsCapturing = true;
                    LogEntries.Clear();
                    _crashDetector.Clear();
                    CrashCount = 0;
                    HasCrashAlert = false;
                    StatusMessage = $"[REC] Auto-capturing - {device.DisplayName} ({device.Serial})";
                    if (!_isSubscribedToLogBatch)
                    {
                        _sessionService.LogBatchReceived += OnLogBatchReceived;
                        _isSubscribedToLogBatch = true;
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"[!] Auto-capture error: {ex.Message}";
            }
            finally
            {
                _autoCaptureInProgress.Remove(device.Serial);
            }
        });
    }
    /// </summary>
    private void OnDeviceDisconnected(DeviceInfo device)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                var stoppedSession = _sessionService.StopCaptureForDevice(device.Serial, Sessions);
                if (stoppedSession != null)
                {
                    if (_isSubscribedToLogBatch)
                    {
                        _sessionService.LogBatchReceived -= OnLogBatchReceived;
                        _isSubscribedToLogBatch = false;
                    }
                    IsCapturing = false;
                    StatusMessage = $"[STOP] Device disconnected. {stoppedSession.LogLineCount} lines captured > {System.IO.Path.GetFileName(stoppedSession.LogFilePath)}";
                    OnPropertyChanged(nameof(SelectedSession));
                }
            }
            catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] Operation failed"); }
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
        catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] Operation failed"); }
    }

    [RelayCommand]
    private async Task StartCapture()
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
                var session = _sessionService.CreateSession(device, NewSessionName);
                Sessions.Insert(0, session);
                SelectedSession = session;
                NewSessionName = string.Empty;
            }

            var started = await _sessionService.StartCaptureAsync(SelectedSession!, SelectedLogBuffer, SelectedLogFormat);
            if (started)
            {
                IsCapturing = true;
                LogEntries.Clear();
                _crashDetector.Clear();
                CrashCount = 0;
                HasCrashAlert = false;
                StatusMessage = $"[REC] Capturing - {device.DisplayName} ({device.Serial})";

                if (!_isSubscribedToLogBatch)
                {
                    _sessionService.LogBatchReceived += OnLogBatchReceived;
                    _isSubscribedToLogBatch = true;
                }
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

    private void OnLogBatchReceived(string sessionId, string batch)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (SelectedSession == null || SelectedSession.Id != sessionId) return;

            var platform = SelectedSession.Platform;
            var lines = batch.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Bulk-parse all lines then AddRange to avoid per-line CollectionChanged events
            var entries = new List<LogEntry>(lines.Length);
            foreach (var line in lines)
            {
                var entry = ParseLogLine(line);
                entries.Add(entry);
                _crashDetector.ScanLine(line, LogEntries.Count + entries.Count - 1, platform);
            }

            LogEntries.AddRange(entries);

            if (LogEntries.Count > 200000)                 TrimLogEntries(150000);

            ScrollToEndRequested?.Invoke();
        });
    }

    private LogEntry ParseLogLine(string rawLine)
    {
        var entry = new LogEntry { RawLine = rawLine, Message = rawLine, Level = LogLevel.Unknown };
        entry.Level = DetectLogLevel(rawLine);

        if (!IsRawMode)
        {
            try
            {
                if (rawLine.StartsWith("["))
                {
                    int closeBracket = rawLine.IndexOf(']');
                    if (closeBracket > 1)
                    {
                        entry.Timestamp = rawLine.Substring(1, closeBracket - 1);
                        entry.Message = rawLine.Substring(closeBracket + 1).TrimStart();
                    }
                }
            }
            catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] Parse failed, keeping raw message"); }
        }
        return entry;
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
        var entry = new LogEntry { RawLine = rawLine, Message = rawLine, Level = LogLevel.Unknown };

        // Always detect log level from raw line for color coding
        entry.Level = DetectLogLevel(rawLine);

        if (!IsRawMode)
        {
            try
            {
                if (rawLine.StartsWith("["))
                {
                    int closeBracket = rawLine.IndexOf(']');
                    if (closeBracket > 1)
                    {
                        entry.Timestamp = rawLine.Substring(1, closeBracket - 1);
                        entry.Message = rawLine.Substring(closeBracket + 1).TrimStart();
                    }
                }
            }
            catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] Parse failed, keeping raw message"); }
        }

        LogEntries.Add(entry);
    }

    // Android threadtime format: "MM-DD HH:MM:SS.mmm  PID  TID L Tag: msg"
    //   level letter sits between TID and Tag, separated by single spaces.
    private static readonly System.Text.RegularExpressions.Regex _logcatThreadtimeRx =
        new(@"^\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+\d+\s+\d+\s+([VDIWEFA])\s",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Android brief/tag format: "L/Tag(pid): msg"  — level letter at index 0, slash at index 1.
    private static readonly System.Text.RegularExpressions.Regex _logcatBriefRx =
        new(@"^([VDIWEFA])/[A-Za-z0-9_\.\-]+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // iOS syslog (pymobiledevice3) emits Apple os_log style:
    //   "<TS> <host> <process>[<pid>] <<Level>>: msg"   — level inside angle brackets
    //   "<TS> ... <Level>: msg"                         — bare bracket-less level token
    private static readonly System.Text.RegularExpressions.Regex _iosSyslogAngleRx =
        new(@"<(Default|Info|Notice|Debug|Error|Fault|Warning)>",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Bracketed leading level: "[ERROR] msg" / "[E] msg".
    private static readonly System.Text.RegularExpressions.Regex _bracketedLevelRx =
        new(@"^\s*\[(?<lvl>FATAL|FTL|ERROR|ERR|WARNING|WARN|INFO|DEBUG|DBG|TRACE|VERBOSE|VRB|F|E|W|I|D|V)\]",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static LogLevel DetectLogLevel(string rawLine)
    {
        if (string.IsNullOrEmpty(rawLine))
            return LogLevel.Unknown;

        var trimmed = rawLine.TrimStart();

        // 1. Android logcat threadtime — most common live-capture format.
        var m = _logcatThreadtimeRx.Match(trimmed);
        if (m.Success) return LetterToLevel(m.Groups[1].Value[0]);

        // 2. Android logcat brief/tag — "E/MyTag(123): msg".
        m = _logcatBriefRx.Match(trimmed);
        if (m.Success) return LetterToLevel(m.Groups[1].Value[0]);

        // 3. iOS syslog with <Level> tag.
        m = _iosSyslogAngleRx.Match(trimmed);
        if (m.Success) return AppleOsLogToLevel(m.Groups[1].Value);

        // 4. Bracketed leading level.
        m = _bracketedLevelRx.Match(trimmed);
        if (m.Success) return TokenToLevel(m.Groups["lvl"].Value);

        // 5. Anchored token at line start (avoid scanning the whole payload — that
        //    misclassifies messages that merely *contain* the word "info" / "error").
        var prefix = trimmed.Length > 16 ? trimmed.Substring(0, 16).ToUpperInvariant() : trimmed.ToUpperInvariant();
        if (StartsWithToken(prefix, "FATAL") || StartsWithToken(prefix, "FTL")) return LogLevel.Fatal;
        if (StartsWithToken(prefix, "ERROR") || StartsWithToken(prefix, "ERR")) return LogLevel.Error;
        if (StartsWithToken(prefix, "WARNING") || StartsWithToken(prefix, "WARN")) return LogLevel.Warning;
        if (StartsWithToken(prefix, "INFO")) return LogLevel.Info;
        if (StartsWithToken(prefix, "DEBUG") || StartsWithToken(prefix, "DBG")) return LogLevel.Debug;
        if (StartsWithToken(prefix, "TRACE") || StartsWithToken(prefix, "VERBOSE") || StartsWithToken(prefix, "VRB")) return LogLevel.Verbose;

        return LogLevel.Unknown;
    }

    private static LogLevel LetterToLevel(char c) => c switch
    {
        'F' or 'A' => LogLevel.Fatal, // 'A' = Assert in some Android logcat builds
        'E' => LogLevel.Error,
        'W' => LogLevel.Warning,
        'I' => LogLevel.Info,
        'D' => LogLevel.Debug,
        'V' => LogLevel.Verbose,
        _ => LogLevel.Unknown
    };

    private static LogLevel AppleOsLogToLevel(string token) => token.ToUpperInvariant() switch
    {
        "FAULT" => LogLevel.Fatal,
        "ERROR" => LogLevel.Error,
        "WARNING" => LogLevel.Warning,
        "NOTICE" or "DEFAULT" or "INFO" => LogLevel.Info,
        "DEBUG" => LogLevel.Debug,
        _ => LogLevel.Unknown
    };

    private static LogLevel TokenToLevel(string token) => token.ToUpperInvariant() switch
    {
        "FATAL" or "FTL" or "F" => LogLevel.Fatal,
        "ERROR" or "ERR" or "E" => LogLevel.Error,
        "WARNING" or "WARN" or "W" => LogLevel.Warning,
        "INFO" or "I" => LogLevel.Info,
        "DEBUG" or "DBG" or "D" => LogLevel.Debug,
        "TRACE" or "VERBOSE" or "VRB" or "V" => LogLevel.Verbose,
        _ => LogLevel.Unknown
    };

    private static bool StartsWithToken(string upper, string token)
    {
        if (!upper.StartsWith(token)) return false;
        // ensure it's a token boundary (next char is non-alpha or end of string)
        if (upper.Length == token.Length) return true;
        var next = upper[token.Length];
        return !(char.IsLetter(next) || next == '_');
    }

    [RelayCommand]
    private void StopCapture()
    {
        try
        {
            if (SelectedSession == null) return;

            if (_isSubscribedToLogBatch)
            {
                _sessionService.LogBatchReceived -= OnLogBatchReceived;
                _isSubscribedToLogBatch = false;
            }
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
            if (!File.Exists(SelectedSession.LogFilePath))
            {
                StatusMessage = "[!] Log file not found on disk.";
                return;
            }
            var rawContent = await File.ReadAllTextAsync(SelectedSession.LogFilePath);
            var path = await _sessionService.SaveLogToFileAsync(SelectedSession, rawContent);
            StatusMessage = $"Log saved: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Save error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        try
        {
            if (SelectedSession == null)
            {
                StatusMessage = "[!] No session selected for export.";
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Log to CSV",
                Filter = "CSV Files (*.csv)|*.csv",
                FileName = $"{SelectedSession.Name}_log.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                StatusMessage = "Exporting to CSV...";
                var success = await _sessionService.ExportToCsvAsync(SelectedSession, dialog.FileName, AnonymizeExport);
                StatusMessage = success 
                    ? $"Exported to: {dialog.FileName}" 
                    : "[!] Export failed.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        try
        {
            if (SelectedSession == null)
            {
                StatusMessage = "[!] No session selected for export.";
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Log to JSON",
                Filter = "JSON Files (*.json)|*.json",
                FileName = $"{SelectedSession.Name}_log.json"
            };

            if (dialog.ShowDialog() == true)
            {
                StatusMessage = "Exporting to JSON...";
                var success = await _sessionService.ExportToJsonAsync(SelectedSession, dialog.FileName, AnonymizeExport);
                StatusMessage = success 
                    ? $"Exported to: {dialog.FileName}" 
                    : "[!] Export failed.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Export error: {ex.Message}";
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

            var deviceHash = Helpers.SecurityHelper.HashSerial(device.Serial);
            var fileName = $"snapshot_{deviceHash}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var outputPath = Path.Combine(saveDir, fileName);

            StatusMessage = "Capturing snapshot...";

            bool success = device.Platform == DevicePlatform.Android
                ? await _adbService.CaptureScreenshotAsync(device.Serial, outputPath)
                : await _iosService.CaptureScreenshotAsync(device.Serial, outputPath);

            if (success)
            {
                StatusMessage = $"Snapshot saved: {fileName}";
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

            var deviceHash = Helpers.SecurityHelper.HashSerial(device.Serial);
            var timestamp = DateTime.Now;

            StatusMessage = "Generating Bug Report...";
            var tempFiles = new List<string>();

            // ── 1. Screenshot ──
            var snapshotName = $"snapshot_{timestamp:yyyyMMdd_HHmmss}.png";
            var snapshotPath = Path.Combine(saveDir, snapshotName);
            if (device.Platform == DevicePlatform.Android)
                await _adbService.CaptureScreenshotAsync(device.Serial, snapshotPath);
            else
                await _iosService.CaptureScreenshotAsync(device.Serial, snapshotPath);
            if (File.Exists(snapshotPath)) tempFiles.Add(snapshotPath);

            // ── 2. Log Dump (last 20k lines + crash snippets) ──
            var logDumpName = $"log_dump_{timestamp:yyyyMMdd_HHmmss}.txt";
            var logDumpPath = Path.Combine(saveDir, logDumpName);
            var logLines = LogEntries.TakeLast(20000).Select(e => e.RawLine).ToList();
            var logContent = string.Join(Environment.NewLine, logLines);

            // Append crash snippets if crashes were detected
            if (_crashDetector.DetectedCrashes.Count > 0)
            {
                logContent += $"\n\n{new string('=', 60)}\n";
                logContent += $"CRASHES DETECTED: {_crashDetector.CrashCount}\n";
                logContent += $"{new string('=', 60)}\n";
                foreach (var crash in _crashDetector.DetectedCrashes)
                {
                    logContent += $"\n--- Crash at {crash.Timestamp:HH:mm:ss.fff} (line #{crash.LineIndex}) ---\n";
                    logContent += $"Pattern: {crash.Pattern}\n";
                    logContent += $"Line: {crash.Line}\n";
                }
            }
            await File.WriteAllTextAsync(logDumpPath, logContent);
            tempFiles.Add(logDumpPath);

            // ── 3. Device Info / Deep Diagnostics ──
            var infoName = $"device_info_{timestamp:yyyyMMdd_HHmmss}.txt";
            var infoPath = Path.Combine(saveDir, infoName);
            var infoContent = new System.Text.StringBuilder();
            infoContent.AppendLine($"=== QADeviceTool BUG REPORT ===");
            infoContent.AppendLine($"Generated: {timestamp:yyyy-MM-dd HH:mm:ss}");
            infoContent.AppendLine($"Device: {device.DisplayName}");
            infoContent.AppendLine($"Serial (hashed): {deviceHash}");
            infoContent.AppendLine($"Platform: {device.Platform}");
            infoContent.AppendLine($"Model: {device.Model}");
            infoContent.AppendLine($"OS: {device.OsVersion}");
            infoContent.AppendLine($"Battery: {device.BatteryLevel}%");
            infoContent.AppendLine($"Session: {SelectedSession?.Name ?? "N/A"}");
            infoContent.AppendLine($"Log entries: {LogEntries.Count}");
            infoContent.AppendLine($"Crashes detected: {_crashDetector.CrashCount}");

            if (device.Platform == DevicePlatform.Android)
            {
                var serial = device.Serial;
                infoContent.AppendLine($"\n{new string('=', 60)}");
                infoContent.AppendLine("SYSTEM PROPERTIES (getprop)");
                infoContent.AppendLine($"{new string('=', 60)}");
                var props = await _adbService.ExecuteCommandAsync(serial, "shell getprop");
                infoContent.AppendLine(props);

                var dumpsysSections = new Dictionary<string, string>
                {
                    ["MEMINFO"] = "shell dumpsys meminfo",
                    ["BATTERY"] = "shell dumpsys battery",
                    ["CPU"] = "shell dumpsys cpuinfo",
                    ["DISK"] = "shell dumpsys diskstats",

                    // PACKAGE section removed — leaks all installed app details including competitor apps
                    ["WINDOW"] = "shell dumpsys window",
                    ["NOTIFICATION"] = "shell dumpsys notification",
                };

                foreach (var (section, cmd) in dumpsysSections)
                {
                    try
                    {
                        var output = await _adbService.ExecuteCommandAsync(serial, cmd);
                        infoContent.AppendLine($"\n{new string('=', 60)}");
                        infoContent.AppendLine($"DUMPSYS {section}");
                        infoContent.AppendLine($"{new string('=', 60)}");
                        infoContent.AppendLine(string.IsNullOrWhiteSpace(output) ? "(empty)" : output);
                    }
                    catch { infoContent.AppendLine($"\n=== {section}: Failed to capture ==="); }
                }

                // Crash buffer logcat
                try
                {
                    var crashLog = await _adbService.ExecuteCommandAsync(serial, "logcat -d -b crash -v threadtime");
                    infoContent.AppendLine($"\n{new string('=', 60)}");
                    infoContent.AppendLine("LOGCAT CRASH BUFFER (-b crash)");
                    infoContent.AppendLine($"{new string('=', 60)}");
                    infoContent.AppendLine(string.IsNullOrWhiteSpace(crashLog) ? "(empty)" : crashLog);
                }
                catch { infoContent.AppendLine("\n=== CRASH BUFFER: Failed ==="); }

                // Tombstone files
                try
                {
                    var tombstones = await _adbService.ExecuteCommandAsync(serial, "shell ls -t /data/tombstones/ 2>/dev/null");
                    if (!string.IsNullOrWhiteSpace(tombstones) && !tombstones.Contains("No such file"))
                    {
                        infoContent.AppendLine($"\n{new string('=', 60)}");
                        infoContent.AppendLine("TOMBSTONE FILES");
                        infoContent.AppendLine($"{new string('=', 60)}");
                        infoContent.AppendLine(tombstones);
                    }
                }
                catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] Tombstone dir access failed"); }

                // ANR traces
                try
                {
                    var anr = await _adbService.ExecuteCommandAsync(serial, "shell ls -t /data/anr/ 2>/dev/null");
                    if (!string.IsNullOrWhiteSpace(anr) && !anr.Contains("No such file"))
                    {
                        infoContent.AppendLine($"\n{new string('=', 60)}");
                        infoContent.AppendLine("ANR TRACES");
                        infoContent.AppendLine($"{new string('=', 60)}");
                        infoContent.AppendLine(anr);
                    }
                }
                catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] ANR dir access failed"); }
            }
            else
            {
                // iOS: include full device info from pymobiledevice3 lockdown info
                var iosDetails = await _iosService.GetDeviceDetailsAsync(device);
                infoContent.AppendLine($"\n{new string('=', 60)}");
                infoContent.AppendLine("iOS DEVICE DETAILS");
                infoContent.AppendLine($"{new string('=', 60)}");
                infoContent.AppendLine($"Name: {iosDetails.Name}");
                infoContent.AppendLine($"Model: {iosDetails.Model}");
                infoContent.AppendLine($"OS: {iosDetails.OsVersion}");
                infoContent.AppendLine($"Serial: {SecurityHelper.HashSerial(iosDetails.Serial)}");

                // iOS Diagnostics (pymobiledevice3)
                try
                {
                    var diag = await _iosService.GetDiagnosticsAsync(device.Serial);
                    infoContent.AppendLine($"\n{new string('=', 60)}");
                    infoContent.AppendLine("iOS DIAGNOSTICS (pymobiledevice3)");
                    infoContent.AppendLine($"{new string('=', 60)}");
                    infoContent.AppendLine(diag);
                }
                catch { infoContent.AppendLine("\nDiagnostics: Failed to capture."); }

                // iOS Crash Logs
                try
                {
                    var crashes = await _iosService.ListCrashLogsAsync(device.Serial);
                    if (crashes.Count > 0)
                    {
                        infoContent.AppendLine($"\n{new string('=', 60)}");
                        infoContent.AppendLine($"CRASH LOGS ({crashes.Count} found)");
                        infoContent.AppendLine($"{new string('=', 60)}");
                        foreach (var c in crashes.Take(20))
                            infoContent.AppendLine(c);
                    }
                }
                catch { infoContent.AppendLine("\nCrash logs: Failed to capture."); }
            }

            await File.WriteAllTextAsync(infoPath, infoContent.ToString());
            tempFiles.Add(infoPath);

            // ── 4. Screen Recording Clip (if available) ──
            if (_lastRecordingPath != null && File.Exists(_lastRecordingPath))
            {
                // Make a copy of the recording in the bug report dir
                var recCopyName = $"screenrecording_{timestamp:yyyyMMdd_HHmmss}.mp4";
                var recCopyPath = Path.Combine(saveDir, recCopyName);
                File.Copy(_lastRecordingPath, recCopyPath, overwrite: true);
                tempFiles.Add(recCopyPath);
            }

            // ── 5. Zip everything ──
            var zipName = $"BugReport_{deviceHash}_{timestamp:yyyyMMdd_HHmmss}.zip";
            var zipPath = Path.Combine(saveDir, zipName);

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var file in tempFiles)
                {
                    if (File.Exists(file))
                        archive.CreateEntryFromFile(file, Path.GetFileName(file));
                }
            }

            // Clean up temp files
            foreach (var file in tempFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] Operation failed"); }
            }

            StatusMessage = $"Bug Report: {zipName} ({tempFiles.Count} artifacts)";
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
            var confirm = MessageBox.Show(
                $"Delete session '{SelectedSession.Name}'? This cannot be undone.",
                "Delete Session", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            if (_isSubscribedToLogBatch)
            {
                _sessionService.LogBatchReceived -= OnLogBatchReceived;
                _isSubscribedToLogBatch = false;
            }
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
            if (SelectedSession == null)
            {
                StatusMessage = "[!] No session selected.";
                return;
            }

            var dir = SelectedSession.SessionDirectory;
            
            if (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(dir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                    StatusMessage = $"Opened: {dir}";
                    return;
                }
                else
                {
                    StatusMessage = $"[!] Session folder not found: {dir}";
                }
            }
            else
            {
                StatusMessage = "[!] Session folder path is empty.";
            }

            var rootDir = Helpers.PathHelper.GetDefaultSessionsDirectory();
            if (Directory.Exists(rootDir))
            {
                System.Diagnostics.Process.Start("explorer.exe", rootDir);
                StatusMessage = $"Opened sessions root: {rootDir}";
            }
            else
            {
                StatusMessage = "[!] Sessions directory not found.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Error opening folder: {ex.Message}";
            Services.AppLogger.Log.Debug(ex, "[SessionViewModel] OpenSessionFolder error");
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        _crashDetector.Clear();
        CrashCount = 0;
        HasCrashAlert = false;
    }

    [RelayCommand]
    private async Task ToggleScreenRecordAsync()
    {
        if (IsScreenRecording)
        {
            await StopScreenRecordAsync();
        }
        else
        {
            // Screen recording can consume ~400MB at 1080p for 3-min max duration
            await StartScreenRecordAsync();
        }
    }

    private async Task StartScreenRecordAsync()
    {
        try
        {
            var device = SelectedDevice;
            if (device == null)
            {
                StatusMessage = "[!] No device selected for screen recording.";
                return;
            }

            if (device.Platform != DevicePlatform.Android)
            {
                StatusMessage = "[!] Screen recording only available for Android on Windows.";
                return;
            }

            var saveDir = SelectedSession?.SessionDirectory
                ?? Helpers.PathHelper.GetDefaultSessionsDirectory();
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);

            _screenRecordRemotePath = await _adbService.StartScreenRecordAsync(
                device.Serial, saveDir, maxDurationSec: 180);

            if (_screenRecordRemotePath != null)
            {
                IsScreenRecording = true;
                ScreenRecordStatus = "[REC] Recording screen...";
                StatusMessage = ScreenRecordStatus;
            }
            else
            {
                StatusMessage = "[!] Failed to start screen recording.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Screen record error: {ex.Message}";
        }
    }

    private async Task StopScreenRecordAsync()
    {
        try
        {
            var device = SelectedDevice;
            if (device == null) return;

            IsScreenRecording = false;
            ScreenRecordStatus = "Saving recording...";
            StatusMessage = ScreenRecordStatus;

            var localPath = await _adbService.StopScreenRecordAsync(device.Serial);

            if (localPath != null && File.Exists(localPath))
            {
                ScreenRecordStatus = string.Empty;
                StatusMessage = $"Screen recording saved: {Path.GetFileName(localPath)}";
                _lastRecordingPath = localPath;
            }
            else
            {
                ScreenRecordStatus = string.Empty;
                StatusMessage = "[!] Failed to save screen recording.";
            }
        }
        catch (Exception ex)
        {
            IsScreenRecording = false;
            ScreenRecordStatus = string.Empty;
            StatusMessage = $"[!] Screen record stop error: {ex.Message}";
        }
    }

    private string? _lastRecordingPath;

    [RelayCommand]
    private void CopyToClipboard()
    {
        try
        {
            var entries = LogEntriesView.Cast<LogEntry>().TakeLast(10000).ToList();             if (entries.Count == 0)             {                 StatusMessage = "[!] No logs to copy.";                 return;             }             var totalCount = LogEntriesView.Cast<LogEntry>().Count();             if (totalCount > entries.Count)                 StatusMessage = $"Copied last {entries.Count} of {totalCount} log entries to clipboard.";             else
            var text = IsRawMode
                ? string.Join(Environment.NewLine, entries.Select(e => e.RawLine))
                : string.Join(Environment.NewLine, entries.Select(e => $"[{e.Timestamp}] [{e.Level}] {e.Message}"));
            Clipboard.SetText(text);
            StatusMessage = $"Copied {entries.Count} log entries to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"[!] Copy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectAllLogLevels()
    {
        foreach (var filter in LogLevelFilters)
            filter.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllLogLevels()
    {
        foreach (var filter in LogLevelFilters)
            filter.IsSelected = false;
    }

    // ── Bookmark Commands ──

    /// <summary>Index of the currently viewed bookmark for navigation.</summary>
    private int _currentBookmarkIndex = -1;

    [RelayCommand]
    private void ToggleBookmark(int logEntryIndex)
    {
        if (logEntryIndex < 0 || logEntryIndex >= LogEntries.Count) return;
        var entry = LogEntries[logEntryIndex];
        entry.IsBookmarked = !entry.IsBookmarked;
        // OnPropertyChanged(nameof(LogEntriesView)); // Not needed with INotifyPropertyChanged on LogEntry
    }

    public int? NextBookmark()
    {
        var bookmarked = LogEntries
            .Select((e, i) => (Entry: e, Index: i))
            .Where(x => x.Entry.IsBookmarked)
            .ToList();

        if (bookmarked.Count == 0) return null;

        _currentBookmarkIndex = (_currentBookmarkIndex + 1) % bookmarked.Count;
        return bookmarked[_currentBookmarkIndex].Index;
    }

    public int? PreviousBookmark()
    {
        var bookmarked = LogEntries
            .Select((e, i) => (Entry: e, Index: i))
            .Where(x => x.Entry.IsBookmarked)
            .ToList();

        if (bookmarked.Count == 0) return null;

        _currentBookmarkIndex--;
        if (_currentBookmarkIndex < 0) _currentBookmarkIndex = bookmarked.Count - 1;
        return bookmarked[_currentBookmarkIndex].Index;
    }

    [RelayCommand]
    private void ClearAllBookmarks()
    {
        foreach (var entry in LogEntries)
            entry.IsBookmarked = false;
        _currentBookmarkIndex = -1;
        LogEntriesView.Refresh();
    }

    private bool FilterLogEntry(object obj)
    {
        if (obj is not LogEntry entry) return false;

        var selectedLevels = LogLevelFilters.Where(f => f.IsSelected).Select(f => f.Level).ToList();

        if (selectedLevels.Count > 0 && !selectedLevels.Contains(entry.Level))
            return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            return entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   entry.Tag.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   entry.RawLine.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
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
            _ = LoadSessionLogSafeAsync(value);
        }
        else
        {
            LogEntries.Clear();
            StatusMessage = "Connect a device and click 'Start Capture' to begin.";
        }
    }

    private async Task LoadSessionLogSafeAsync(LogSession session)
    {
        if (_isLoadingSession) return;
        _isLoadingSession = true;

        try
        {
            if (string.IsNullOrEmpty(session.LogFilePath) || !File.Exists(session.LogFilePath))
            {
                if (!string.IsNullOrEmpty(session.SessionDirectory) && Directory.Exists(session.SessionDirectory))
                {
                    var logFiles = Directory.GetFiles(session.SessionDirectory, "*.txt")
                        .Concat(Directory.GetFiles(session.SessionDirectory, "*.log"))
                        .ToArray();

                    if (logFiles.Length > 0)
                    {
                        session.LogFilePath = logFiles[0];
                    }
                    else
                    {
                        await _dispatcher.BeginInvoke(() =>
                        {
                            LogEntries.Clear();
                            StatusMessage = session.Status == SessionStatus.Idle
                                ? "Ready to capture. Click 'Start' to begin."
                                : "No log file found.";
                        });
                        return;
                    }
                }
                else
                {
                    await _dispatcher.BeginInvoke(() =>
                    {
                        LogEntries.Clear();
                        StatusMessage = session.Status == SessionStatus.Idle
                            ? "Ready to capture. Click 'Start' to begin."
                            : "No log file found.";
                    });
                    return;
                }
            }

            await _dispatcher.BeginInvoke(() => StatusMessage = "Loading log...");
            var content = await _sessionService.ReadLogContentAsync(session, maxLines: 200000);

            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            await _dispatcher.BeginInvoke(() =>
            {
                var parsed = lines.Select(ParseLogLine).ToList();
                LogEntries.Clear();
                LogEntries.AddRange(parsed);

                if (LogEntries.Count > 200000)                     TrimLogEntries(150000);
                }

                StatusMessage = $"Loaded {LogEntries.Count} log entries.";
            });
        }
        catch (Exception ex)
        {
            await _dispatcher.BeginInvoke(() =>
                StatusMessage = $"Could not load log file: {ex.Message}");
        }
        finally
        {
            _isLoadingSession = false;
        }
    }

    private void TrimLogEntries(int maxEntries)
    {
        if (LogEntries.Count <= maxEntries) return;
        var removeCount = LogEntries.Count - maxEntries;
        LogEntries.RemoveRange(0, removeCount);
    }

    public void Dispose()
    {
            _deviceMonitor.DevicesChanged -= OnDevicesChanged;
            _deviceMonitor.DeviceConnected -= OnDeviceConnected;
            _deviceMonitor.DeviceDisconnected -= OnDeviceDisconnected;
        GC.SuppressFinalize(this);
    }
}

