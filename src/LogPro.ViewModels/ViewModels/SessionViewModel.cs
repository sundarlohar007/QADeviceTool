using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogPro.Models;
using System.Text.RegularExpressions;
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
    private readonly IUiDispatcher _dispatcher;
    private readonly BugReportService _bugReportService;
    private int _pendingLogUiUpdates;
    private long _displaySkipped;

    // ── Log Viewer Properties ──
    public BulkObservableCollection<LogEntry> LogEntries { get; } = new();
    public BulkObservableCollection<LogEntry> LogEntriesView { get; } = new();

    // UI scroll scroll-to-end event
    public event Action? ScrollToEndRequested;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LogLevel _selectedLogLevel = LogLevel.Verbose;

    public Array LogLevels => Enum.GetValues(typeof(LogLevel));
    public Array LogcatBuffers => Enum.GetValues(typeof(LogcatBuffer));
    public Array LogcatFormats => Enum.GetValues(typeof(LogcatFormat));
    public string PauseDisplayLabel => IsPaused ? "Resume display" : "Pause display";
    public string CaptureHealthText
    {
        get
        {
            if (SelectedSession == null) return "No session selected";
            var stats = _sessionService.GetCaptureStatistics(SelectedSession.Id);
            return $"Captured {SelectedSession.LogLineCount:N0} | showing {LogEntriesView.Count:N0}" +
                (SelectedSession.Status == SessionStatus.Capturing
                    ? $" | pending {stats.PendingLines:N0} | display skipped {stats.DroppedLines + Interlocked.Read(ref _displaySkipped):N0}" : string.Empty) +
                (IsPaused ? " | display paused" : string.Empty) +
                (string.IsNullOrEmpty(SelectedSession.CaptureNotice) ? "" : " | " + SelectedSession.CaptureNotice);
        }
    }
    public string SelectedSessionDetails => SelectedSession == null ? "Select a session to see its details." :
        $"{SelectedSession.Platform} | device {(string.IsNullOrEmpty(SelectedSession.DeviceId) ? "unknown" : SelectedSession.DeviceId)} | {SelectedSession.Status}\n" +
        $"Started {SelectedSession.StartTime:g}" +
        (SelectedSession.EndTime is { } end ? $" | ended {end:g}" : string.Empty) +
        $"\n{SelectedSession.LogFilePath}";

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
    private bool _anonymizeExport = true;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _availableDevices = new();

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private bool _isPaused;

    private bool _isSubscribedToLogBatch;
    private int _disposed;
    private readonly HashSet<string> _autoCaptureInProgress = new();
    private readonly object _autoCaptureLock = new();
    private int _loadGeneration;
    private string? _lastRecordingSessionId;
    private string? _lastRecordingSerial;
    private CancellationTokenSource? _fullSearchCts;
    [ObservableProperty]
    private bool _isShowingFullFileSearch;
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
    private string? _screenRecordSerial;
    private string? _screenRecordSessionId;

    [ObservableProperty]
    private bool _autoCapture;

    [ObservableProperty]
    private bool _isColorCodingEnabled = true;

    [ObservableProperty]
    private bool _isRegexSearch = false;

    [ObservableProperty]
    private bool _isRawMode = true;

    [ObservableProperty]
    private bool _showBookmarksOnly;

    [ObservableProperty]
    private bool _isAutoScrollEnabled = true;

    [ObservableProperty]
    private LogcatBuffer _selectedLogBuffer = LogcatBuffer.Main;

    [ObservableProperty]
    private LogcatFormat _selectedLogFormat = LogcatFormat.ThreadTime;

    public SessionViewModel(ISessionService sessionService, IAdbService adbService, IIosService iosService, IDeviceMonitorService deviceMonitor, IUiDispatcher? dispatcher = null)
    {
        _sessionService = sessionService;
        _adbService = adbService;
        _iosService = iosService;
        _deviceMonitor = deviceMonitor;
        _bugReportService = new BugReportService(adbService, iosService);
        _dispatcher = dispatcher ?? UiServices.Dispatcher;

        InitializeLogLevelFilters();

        _deviceMonitor.DevicesChanged += OnDevicesChanged;
        _deviceMonitor.DeviceConnected += OnDeviceConnected;
        _deviceMonitor.DeviceDisconnected += OnDeviceDisconnected;
        _sessionService.CaptureStarted += OnCaptureStarted;
        _sessionService.CaptureStopped += OnCaptureStopped;

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
        _dispatcher.Post(() =>
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
                    _selectedLevelsCache = null; // invalidate cache
                    RebuildFilteredView();
                }
            };
        }
    }

    private List<LogLevel>? _selectedLevelsCache;
    private List<LogLevel> SelectedLevels =>
        _selectedLevelsCache ??= LogLevelFilters.Where(f => f.IsSelected).Select(f => f.Level).ToList();

    private void OnDevicesChanged(List<DeviceInfo> devices)
    {
        _dispatcher.Post(() =>
        {
            var selectedSerial = SelectedDevice?.Serial;
            var selectedPlatform = SelectedDevice?.Platform;
            AvailableDevices.Clear();
            foreach (var d in devices)
                AvailableDevices.Add(d);

            SelectedDevice = devices.FirstOrDefault(d =>
                d.Serial == selectedSerial && d.Platform == selectedPlatform)
                ?? devices.FirstOrDefault();
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
        if (!AutoCapture || device.ConnectionState != DeviceConnectionState.Online) return;

        _dispatcher.Post(async () =>
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
                    if (SelectedSession?.Id == session.Id)
                    {
                        IsCapturing = true;
                        LogEntries.Clear();
                        LogEntriesView.Clear();
                        _crashDetector.Clear();
                        CrashCount = 0;
                        HasCrashAlert = false;
                        StatusMessage = $"[REC] Auto-capturing - {device.DisplayName} ({device.Serial})";
                    }
                    if (!_isSubscribedToLogBatch)
                    {
                        _sessionService.LogBatchReceived += OnLogBatchReceived;
                        _isSubscribedToLogBatch = true;
                    }
                }
                else if (SelectedSession?.Id == session.Id)
                    StatusMessage = $"[!] Auto-capture could not start for {device.DisplayName}. Check the device and platform tools.";
            }
            catch (Exception ex)
            {
                AppLogger.Log.Error(ex, "[Session] Auto-capture failed");
                StatusMessage = $"[!] Auto-capture error: {ex.Message}";
            }
            finally
            {
                lock (_autoCaptureLock)
                {
                    _autoCaptureInProgress.Remove(device.Serial);
                }
            }
        });
    }
    /// </summary>
    private void OnDeviceDisconnected(DeviceInfo device)
    {
        _dispatcher.Post(() =>
        {
            try
            {
                var stoppedSession = _sessionService.GetActiveSessionForDevice(device.Serial);
                if (stoppedSession != null)
                {
                    _sessionService.StopCapture(stoppedSession);
                    if (SelectedSession?.Id == stoppedSession.Id)
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
            var active = _sessionService.ActiveSessions;
            var saved = _sessionService.GetSavedSessions();
            Sessions.Clear();
            foreach (var session in active)
                Sessions.Add(session);
            foreach (var s in saved)
                if (!active.Any(a => string.Equals(a.SessionDirectory, s.SessionDirectory, StringComparison.OrdinalIgnoreCase)))
                    Sessions.Add(s);
            if (active.Count > 0)
            {
                SelectedSession = active[0];
                IsCapturing = true;
                if (!_isSubscribedToLogBatch)
                {
                    _sessionService.LogBatchReceived += OnLogBatchReceived;
                    _isSubscribedToLogBatch = true;
                }
            }
        }
        catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] Operation failed"); }
    }

    private void OnCaptureStarted(LogSession session)
    {
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Sessions.All(s => s.Id != session.Id))
            {
                Sessions.Insert(0, session);
                SelectedSession = session;
            }
            if (SelectedSession?.Id == session.Id)
                IsCapturing = true;
            if (!_isSubscribedToLogBatch)
            {
                _sessionService.LogBatchReceived += OnLogBatchReceived;
                _isSubscribedToLogBatch = true;
            }
        });
    }

    private void OnCaptureStopped(LogSession session)
    {
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (SelectedSession?.Id == session.Id)
            {
                IsCapturing = false;
                if (!string.IsNullOrWhiteSpace(session.CaptureError)) StatusMessage = session.CaptureError;
            }
            OnPropertyChanged(nameof(CaptureHealthText));
            OnPropertyChanged(nameof(SelectedSessionDetails));
        });
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
            if (device.ConnectionState != DeviceConnectionState.Online || device.IsTemporarilyUnavailable)
            {
                StatusMessage = $"[!] {device.DisplayName} is {device.StatusText}. Connect and authorize it before capture.";
                return;
            }

            if (SelectedSession == null || SelectedSession.Status != SessionStatus.Idle ||
                !string.Equals(SelectedSession.DeviceSerial, device.Serial, StringComparison.OrdinalIgnoreCase))
            {
                var session = _sessionService.CreateSession(device, NewSessionName);
                Sessions.Insert(0, session);
                SelectedSession = session;
                NewSessionName = string.Empty;
            }

            var captureSession = SelectedSession!;
            var started = await _sessionService.StartCaptureAsync(captureSession, SelectedLogBuffer, SelectedLogFormat);
            if (started)
            {
                if (SelectedSession?.Id == captureSession.Id)
                {
                    IsCapturing = true;
                    LogEntries.Clear();
                    LogEntriesView.Clear();
                    _crashDetector.Clear();
                    CrashCount = 0;
                    HasCrashAlert = false;
                    StatusMessage = $"[REC] Capturing - {device.DisplayName} ({device.Serial})";
                }

                if (!_isSubscribedToLogBatch)
                {
                    _sessionService.LogBatchReceived += OnLogBatchReceived;
                    _isSubscribedToLogBatch = true;
                }
            }
            else
            {
                StatusMessage = "[!] " + captureSession.CaptureError;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] StartCapture failed");
            StatusMessage = $"[!] Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AnalyzeWithAI() { }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused) RebuildFilteredView();
        StatusMessage = IsPaused ? "Live display paused; capture continues to disk." : "Live display resumed.";
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseDisplayLabel));
        OnPropertyChanged(nameof(CaptureHealthText));
    }

    private void OnLogBatchReceived(string sessionId, string batch)
    {
        if (Volatile.Read(ref _disposed) != 0 || SelectedSession?.Id != sessionId) return;
        var lines = batch.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (Interlocked.Increment(ref _pendingLogUiUpdates) > 4)
        {
            Interlocked.Decrement(ref _pendingLogUiUpdates);
            Interlocked.Add(ref _displaySkipped, lines.Length);
            return; // The full capture remains on disk; never let UI work grow without bound.
        }
        // SessionService delivers on its background flush thread. Parse before dispatching.
        var rawMode = IsRawMode;
        var dispatched = false;
        try
        {
            var entries = lines.Select(line => ParseLogLine(line, rawMode)).ToList();
            _dispatcher.Post(() =>
            {
                dispatched = true;
                try
                {
                    if (Volatile.Read(ref _disposed) != 0 || SelectedSession?.Id != sessionId) return;
                    for (var i = 0; i < lines.Length; i++)
                        _crashDetector.ScanLine(lines[i], LogEntries.Count + i, SelectedSession.Platform);
                    LogEntries.AddRange(entries);
                    if (!IsPaused) LogEntriesView.AddRange(entries.Where(FilterLogEntry));
                    if (LogEntries.Count > 200000) TrimLogEntries(150000);
                    if (!IsPaused) ScrollToEndRequested?.Invoke();
                    OnPropertyChanged(nameof(CaptureHealthText));
                }
                finally { Interlocked.Decrement(ref _pendingLogUiUpdates); }
            });
        }
        catch
        {
            if (!dispatched) Interlocked.Decrement(ref _pendingLogUiUpdates);
            throw;
        }
    }

    private LogEntry ParseLogLine(string rawLine, bool? rawMode = null)
    {
        var entry = new LogEntry { RawLine = rawLine, Message = rawLine, Level = LogLevel.Unknown };
        entry.Level = DetectLogLevel(rawLine);

        if (!(rawMode ?? IsRawMode))
        {
            try
            {
                var android = _logcatStructuredRx.Match(rawLine);
                if (android.Success)
                {
                    entry.Timestamp = android.Groups["timestamp"].Value;
                    entry.Tag = android.Groups["tag"].Value.Trim();
                    entry.Message = android.Groups["message"].Value;
                }
                else if (rawLine.StartsWith("["))
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

    private static readonly Regex _logcatStructuredRx = new(
        @"^(?<timestamp>\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s+\d+\s+\d+\s+[VDIWEFA]\s+(?<tag>[^:]+):\s*(?<message>.*)$",
        RegexOptions.Compiled);

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

            _sessionService.StopCapture(SelectedSession);
            IsCapturing = false;
            StatusMessage = $"[STOP] Stopped. {SelectedSession.LogLineCount} lines captured > {Path.GetFileName(SelectedSession.LogFilePath)}";
            OnPropertyChanged(nameof(SelectedSession));
            OnPropertyChanged(nameof(CaptureHealthText));
            OnPropertyChanged(nameof(SelectedSessionDetails));
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] StopCapture failed");
            StatusMessage = $"[!] Error stopping: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        try
        {
            var session = SelectedSession;
            if (session == null)
            {
                StatusMessage = "[!] No active session to save.";
                return;
            }
            if (!File.Exists(session.LogFilePath))
            {
                StatusMessage = "[!] Log file not found on disk.";
                return;
            }
            var path = await _sessionService.SaveLogCopyAsync(session);
            StatusMessage = path.StartsWith("Error:", StringComparison.Ordinal)
                ? $"[!] {path}" : $"Log saved: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] SaveLogAsync failed");
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

            var csvPath = UiServices.Files.SaveFile("Export Log to CSV", "CSV Files (*.csv)|*.csv", $"{SelectedSession.Name}_log.csv");
            if (csvPath != null)
            {
                StatusMessage = "Exporting to CSV...";
                var success = await _sessionService.ExportToCsvAsync(SelectedSession, csvPath, AnonymizeExport);
                StatusMessage = success
                    ? $"Exported to: {csvPath}"
                    : "[!] Export failed.";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] ExportCsvAsync failed");
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

            var jsonPath = UiServices.Files.SaveFile("Export Log to JSON", "JSON Files (*.json)|*.json", $"{SelectedSession.Name}_log.json");
            if (jsonPath != null)
            {
                StatusMessage = "Exporting to JSON...";
                var success = await _sessionService.ExportToJsonAsync(SelectedSession, jsonPath, AnonymizeExport);
                StatusMessage = success
                    ? $"Exported to: {jsonPath}"
                    : "[!] Export failed.";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] ExportJsonAsync failed");
            StatusMessage = $"[!] Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TakeSnapshotAsync()
    {
        try
        {
            var device = ResolveArtifactDevice();
            if (device == null)
            {
                StatusMessage = SelectedSession == null
                    ? "[!] Select a connected device for snapshot."
                    : "[!] The selected session's device is not connected for snapshot.";
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
                saveDir = _sessionService.SessionsRootDirectory;
            }

            var deviceHash = Helpers.SecurityHelper.HashSerial(device.Serial);
            var fileName = $"snapshot_{deviceHash}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png";
            var outputPath = Path.Combine(saveDir, fileName);

            StatusMessage = "Capturing snapshot...";

            var iosResult = device.Platform == DevicePlatform.iOS
                ? await _iosService.CaptureScreenshotWithStatusAsync(device.Serial, outputPath)
                : default;
            bool success = device.Platform == DevicePlatform.Android
                ? await _adbService.CaptureScreenshotAsync(device.Serial, outputPath)
                : iosResult.Success;

            if (success)
            {
                StatusMessage = $"Snapshot saved: {fileName}";
            }
            else
            {
                StatusMessage = device.Platform == DevicePlatform.iOS ? $"[!] {iosResult.Message}" : "[!] Snapshot failed. Check device connection.";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] TakeSnapshotAsync failed");
            StatusMessage = $"[!] Snapshot error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task GenerateBugReportAsync()
    {
        try
        {
            var session = SelectedSession;
            var device = ResolveArtifactDevice();
            if (device == null)
            {
                StatusMessage = session == null
                    ? "[!] Select a connected device for bug report."
                    : "[!] The selected session's device is not connected for bug report.";
                return;
            }

            var saveDir = session != null && !string.IsNullOrEmpty(session.SessionDirectory)
                ? session.SessionDirectory
                : _sessionService.SessionsRootDirectory;

            var recording = _lastRecordingSerial == device.Serial &&
                _lastRecordingSessionId == session?.Id ? _lastRecordingPath : null;
            var crashes = _crashDetector.DetectedCrashes;
            if (!UiServices.Dialogs.Confirm("Bug Report Preview",
                $"Device: {device.DisplayName}\nSession: {session?.Name ?? "None"}\n" +
                $"Includes screenshot, recent logs, crash alerts, device diagnostics" +
                (recording == null ? "." : ", and a screen recording.") +
                "\nScreenshots and recordings can show on-screen personal information.")) return;

            StatusMessage = "Generating Bug Report...";
            var logLines = session != null && File.Exists(session.LogFilePath)
                ? (await _sessionService.ReadLogContentAsync(session, 20000))
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList()
                : LogEntries.TakeLast(20000).Select(e => e.RawLine).ToList();
            var (success, message) = await _bugReportService.GenerateAsync(
                device, saveDir, session?.Name ?? "N/A",
                logLines, crashes, recording);
            StatusMessage = message;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] GenerateBugReportAsync failed");
            StatusMessage = $"[!] Bug Report error: {ex.Message}";
        }
    }

    private DeviceInfo? ResolveArtifactDevice()
    {
        if (SelectedSession == null)
            return SelectedDevice == null
                ? AvailableDevices.FirstOrDefault(d => d.ConnectionState == DeviceConnectionState.Online)
                : SelectedDevice.ConnectionState == DeviceConnectionState.Online ? SelectedDevice : null;
        var match = AvailableDevices.FirstOrDefault(d =>
            d.ConnectionState == DeviceConnectionState.Online &&
            ((!string.IsNullOrEmpty(SelectedSession.DeviceSerial) &&
             string.Equals(d.Serial, SelectedSession.DeviceSerial, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(SelectedSession.DeviceId) &&
             Helpers.SecurityHelper.HashSerial(d.Serial) == SelectedSession.DeviceId)));
        if (match == null)
            StatusMessage = "[!] The selected session's device is not connected.";
        return match;
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

            var session = _sessionService.CreateSession(device, NewSessionName);
            Sessions.Insert(0, session);
            SelectedSession = session;
            NewSessionName = string.Empty;
            StatusMessage = $"Session '{session.Name}' created.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] CreateSession failed");
            StatusMessage = $"[!] Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteSession()
    {
        try
        {
            if (SelectedSession == null) return;
            if (SelectedSession.Status == SessionStatus.Capturing)
            {
                StatusMessage = "[!] Stop the active capture before deleting this session.";
                return;
            }
            var confirm = UiServices.Dialogs.Confirm(
                "Delete Session",
                $"Delete session '{SelectedSession.Name}'? This cannot be undone.");
            if (!confirm) return;
            if (!_sessionService.DeleteSession(SelectedSession))
            {
                StatusMessage = "[!] Could not delete the session folder.";
                return;
            }
            Sessions.Remove(SelectedSession);
            SelectedSession = null;
            IsCapturing = false;
            LogEntries.Clear();
            StatusMessage = "Session deleted.";
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] DeleteSession failed");
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

            var rootDir = _sessionService.SessionsRootDirectory;
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
        LogEntriesView.Clear();
        _crashDetector.Clear();
        CrashCount = 0;
        HasCrashAlert = false;
        OnPropertyChanged(nameof(CaptureHealthText));
        StatusMessage = "Viewer cleared; captured log file was retained.";
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
            var session = SelectedSession;
            var device = ResolveArtifactDevice();
            if (device == null)
            {
                StatusMessage = session == null
                    ? "[!] Select a connected device for screen recording."
                    : "[!] The selected session's device is not connected for recording.";
                return;
            }

            if (device.Platform != DevicePlatform.Android)
            {
                StatusMessage = "[!] Screen recording only available for Android on Windows.";
                return;
            }

            var sdk = await _adbService.GetDevicePropertyAsync(device.Serial, "ro.build.version.sdk");
            if (int.TryParse(sdk, out var apiLevel) && apiLevel < 19)
            {
                StatusMessage = "[!] Screen recording requires Android 4.4 (API 19) or later.";
                return;
            }
            var characteristics = await _adbService.GetDevicePropertyAsync(device.Serial, "ro.build.characteristics");
            if (characteristics?.Split(',').Any(c => c.Trim().Equals("watch", StringComparison.OrdinalIgnoreCase)) == true)
            {
                StatusMessage = "[!] Standard ADB screen recording is not supported on Wear OS devices.";
                return;
            }

            var saveDir = session?.SessionDirectory
                ?? _sessionService.SessionsRootDirectory;
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);

            _screenRecordRemotePath = await _adbService.StartScreenRecordAsync(
                device.Serial, saveDir, maxDurationSec: 180);

            if (_screenRecordRemotePath != null)
            {
                _screenRecordSerial = device.Serial;
                _screenRecordSessionId = session?.Id;
                IsScreenRecording = true;
                ScreenRecordStatus = "[REC] Recording screen (no audio; 3-minute maximum)...";
                StatusMessage = ScreenRecordStatus;
                _ = MonitorScreenRecordingAsync(device.Serial);
            }
            else
            {
                StatusMessage = "[!] Failed to start screen recording.";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] StartScreenRecordAsync failed");
            StatusMessage = $"[!] Screen record error: {ex.Message}";
        }
    }

    private async Task MonitorScreenRecordingAsync(string serial)
    {
        try
        {
            while (IsScreenRecording && _screenRecordSerial == serial && Volatile.Read(ref _disposed) == 0)
            {
                await Task.Delay(2000);
                if (IsScreenRecording && _screenRecordSerial == serial && !_adbService.IsScreenRecording)
                {
                    await StopScreenRecordAsync();
                    return;
                }
            }
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[Session] Recording monitor failed"); }
    }

    private async Task StopScreenRecordAsync()
    {
        try
        {
            var serial = _screenRecordSerial;
            if (serial == null) return;
            var recordingSessionId = _screenRecordSessionId;

            IsScreenRecording = false;
            ScreenRecordStatus = "Saving recording...";
            StatusMessage = ScreenRecordStatus;

            var localPath = await _adbService.StopScreenRecordAsync(serial);
            _screenRecordSerial = null;
            _screenRecordSessionId = null;

            if (localPath != null && File.Exists(localPath))
            {
                ScreenRecordStatus = string.Empty;
                StatusMessage = $"Screen recording saved: {Path.GetFileName(localPath)}";
                _lastRecordingPath = localPath;
                _lastRecordingSerial = serial;
                _lastRecordingSessionId = recordingSessionId;
            }
            else
            {
                ScreenRecordStatus = string.Empty;
                StatusMessage = "[!] Failed to save screen recording.";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] StopScreenRecordAsync failed");
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
            var entries = LogEntriesView.TakeLast(10000).ToList();
            if (entries.Count == 0)
            {
                StatusMessage = "[!] No logs to copy.";
                return;
            }
            var totalCount = LogEntriesView.Count;
            if (totalCount > entries.Count)
                StatusMessage = $"Copied last {entries.Count} of {totalCount} log entries to clipboard. (Truncated for performance)";
            else
                StatusMessage = $"Copied {entries.Count} log entries to clipboard.";

            var text = IsRawMode
                ? string.Join(Environment.NewLine, entries.Select(e => e.RawLine))
                : string.Join(Environment.NewLine, entries.Select(e => $"[{e.Timestamp}] [{e.Level}] {e.Message}"));

            UiServices.Clipboard.SetText(Helpers.SecurityHelper.RedactSensitiveText(text));
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] CopyToClipboard failed");
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
    private void ToggleBookmark(LogEntry entry)
    {
        if (!LogEntries.Contains(entry)) return;
        entry.IsBookmarked = !entry.IsBookmarked;
        if (ShowBookmarksOnly) RebuildFilteredView();
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
        RebuildFilteredView();
    }

    private bool FilterLogEntry(object obj)
    {
        if (obj is not LogEntry entry) return false;
        if (ShowBookmarksOnly && !entry.IsBookmarked) return false;

        var selectedLevels = SelectedLevels;

        if (selectedLevels.Count > 0 && !selectedLevels.Contains(entry.Level))
            return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            if (IsRegexSearch)
            {
                try
                {
                    if (_cachedSearchPattern != SearchText)
                    {
                        _cachedSearchPattern = SearchText;
                        _searchRegexTimedOut = false;
                        _searchRegexInvalid = false;
                        _cachedSearchRegex = null;
                        _cachedSearchRegex = new Regex(SearchText,
                            RegexOptions.IgnoreCase | RegexOptions.Compiled,
                            TimeSpan.FromMilliseconds(25));
                    }
                    return !_searchRegexTimedOut && !_searchRegexInvalid &&
                        _cachedSearchRegex?.IsMatch(entry.RawLine) == true;
                }
                catch (RegexMatchTimeoutException)
                {
                    _searchRegexTimedOut = true;
                    StatusMessage = "[!] Regex search timed out. Use a simpler expression.";
                    return false;
                }
                catch (ArgumentException)
                {
                    _searchRegexInvalid = true;
                    return false;
                }
            }

            return entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   entry.Tag.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   entry.RawLine.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    partial void OnIsRegexSearchChanged(bool value)
    {
        _cachedSearchPattern = null;
        RebuildFilteredView();
    }

    partial void OnShowBookmarksOnlyChanged(bool value) => RebuildFilteredView();

    partial void OnIsRawModeChanged(bool value)
    {
        foreach (var entry in LogEntries)
        {
            var parsed = ParseLogLine(entry.RawLine);
            entry.Timestamp = parsed.Timestamp;
            entry.Level = parsed.Level;
            entry.Tag = parsed.Tag;
            entry.Message = parsed.Message;
        }
        RebuildFilteredView();
    }

    private Regex? _cachedSearchRegex;
    private string? _cachedSearchPattern;
    private bool _searchRegexTimedOut;
    private bool _searchRegexInvalid;
    private CancellationTokenSource? _searchDebounceCts;

    partial void OnSearchTextChanged(string value)
    {
        _cachedSearchPattern = null;
        // FEAT-03: debounce 300ms — avoid re-filtering 100k+ entries on every keystroke
        var oldCts = _searchDebounceCts;
        _searchDebounceCts = new CancellationTokenSource();
        try { oldCts?.Cancel(); } catch { /* best effort */ }
        try { oldCts?.Dispose(); } catch { /* best effort */ }
        var token = _searchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                if (!token.IsCancellationRequested)
                    _dispatcher.Post(RebuildFilteredView);
            }
            catch (OperationCanceledException) { /* debounced */ }
            catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] Search debounce error"); }
        });
    }

    partial void OnSelectedLogLevelChanged(LogLevel value)
    {
        RebuildFilteredView();
    }

    partial void OnSelectedSessionChanged(LogSession? value)
    {
        _fullSearchCts?.Cancel();
        IsShowingFullFileSearch = false;
        Interlocked.Increment(ref _loadGeneration);
        IsCapturing = value?.Status == SessionStatus.Capturing;
        OnPropertyChanged(nameof(SelectedSessionDetails));
        OnPropertyChanged(nameof(CaptureHealthText));
        IsPaused = false;
        _crashDetector.Clear();
        CrashCount = 0;
        HasCrashAlert = false;
        if (value != null)
        {
            _ = LoadSessionLogSafeAsync(value, Volatile.Read(ref _loadGeneration));
        }
        else
        {
            LogEntries.Clear();
            LogEntriesView.Clear();
            StatusMessage = "Connect a device and click 'Start Capture' to begin.";
        }
    }

    private async Task LoadSessionLogSafeAsync(LogSession session, int generation)
    {
        try
        {
            if (string.IsNullOrEmpty(session.LogFilePath) || !File.Exists(session.LogFilePath))
            {
                if (!string.IsNullOrEmpty(session.SessionDirectory) && Directory.Exists(session.SessionDirectory))
                {
                    var logFiles = Directory.GetFiles(session.SessionDirectory, "*_log.txt")
                        .Where(f => !f.EndsWith("_app_log.txt", StringComparison.OrdinalIgnoreCase))
                        .Concat(Directory.GetFiles(session.SessionDirectory, "*.log"))
                        .Concat(Directory.GetFiles(session.SessionDirectory, "manual_log_*.txt"))
                        .Concat(Directory.GetFiles(session.SessionDirectory, "saved_log_*.txt"))
                        .ToArray();

                    if (logFiles.Length > 0)
                    {
                        session.LogFilePath = logFiles[0];
                    }
                    else
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (generation != Volatile.Read(ref _loadGeneration)) return;
                            LogEntries.Clear();
                            LogEntriesView.Clear();
                            StatusMessage = session.Status == SessionStatus.Idle
                                ? "Ready to capture. Click 'Start' to begin."
                                : "No log file found.";
                        });
                        return;
                    }
                }
                else
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (generation != Volatile.Read(ref _loadGeneration)) return;
                        LogEntries.Clear();
                        LogEntriesView.Clear();
                        StatusMessage = session.Status == SessionStatus.Idle
                            ? "Ready to capture. Click 'Start' to begin."
                            : "No log file found.";
                    });
                    return;
                }
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (generation == Volatile.Read(ref _loadGeneration)) StatusMessage = "Loading log...";
            });
            if (Volatile.Read(ref _disposed) != 0) return;
            var content = await _sessionService.ReadLogContentAsync(session, maxLines: 200000);

            if (Volatile.Read(ref _disposed) != 0 || generation != Volatile.Read(ref _loadGeneration)) return;
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var rawMode = IsRawMode;
            var parsed = await Task.Run(() => lines.Select(line => ParseLogLine(line, rawMode)).ToList());

            await _dispatcher.InvokeAsync(() =>
            {
                if (generation != Volatile.Read(ref _loadGeneration) || SelectedSession?.Id != session.Id) return;
                LogEntries.Clear();
                LogEntries.AddRange(parsed);
                RebuildFilteredView();

                if (LogEntries.Count > 200000)
                    TrimLogEntries(150000);

                StatusMessage = $"Loaded {LogEntries.Count} log entries.";
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[Session] LoadSessionLogSafeAsync failed");
            await _dispatcher.InvokeAsync(() =>
            {
                if (generation == Volatile.Read(ref _loadGeneration))
                    StatusMessage = $"Could not load log file: {ex.Message}";
            });
        }

    }

    private void TrimLogEntries(int maxEntries)
    {
        if (LogEntries.Count <= maxEntries) return;
        var removeCount = LogEntries.Count - maxEntries;
        LogEntries.RemoveRange(0, removeCount);
        // The filtered view also holds trimmed entries — rebuild it (rare, cap-only path).
        RebuildFilteredView();
    }

    /// <summary>Re-applies the active filter over the whole base collection (used on filter changes).</summary>
    private void RebuildFilteredView()
    {
        var filtered = new List<LogEntry>();
        foreach (var entry in LogEntries)
        {
            if (FilterLogEntry(entry))
                filtered.Add(entry);
        }
        LogEntriesView.Clear();
        LogEntriesView.AddRange(filtered);
        OnPropertyChanged(nameof(CaptureHealthText));
    }

    [RelayCommand]
    private async Task SearchEntireSessionAsync()
    {
        var session = SelectedSession;
        var query = SearchText;
        if (session == null || session.Status == SessionStatus.Capturing ||
            string.IsNullOrWhiteSpace(query) || !File.Exists(session.LogFilePath))
        {
            StatusMessage = "[!] Select a stopped session and enter a search term.";
            return;
        }
        _fullSearchCts?.Cancel();
        _fullSearchCts = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _loadGeneration);
        var token = _fullSearchCts.Token;
        Regex? regex = null;
        try
        {
            if (IsRegexSearch)
                regex = new Regex(query, RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(25));
            StatusMessage = "Searching complete session file...";
            var matches = await Task.Run(async () =>
            {
                var results = new List<string>();
                await using var stream = new FileStream(session.LogFilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    if (regex?.IsMatch(line) ?? line.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(line);
                        if (results.Count == 5000) break;
                    }
                }
                return results;
            }, token);
            if (SelectedSession?.Id != session.Id || token.IsCancellationRequested ||
                generation != Volatile.Read(ref _loadGeneration)) return;
            IsShowingFullFileSearch = true;
            LogEntries.Clear();
            LogEntries.AddRange(matches.Select(line => ParseLogLine(line)).ToList());
            RebuildFilteredView();
            StatusMessage = matches.Count == 5000
                ? "Showing first 5,000 file matches. Narrow the search to see more."
                : $"Found {matches.Count:N0} matches in the complete session file.";
        }
        catch (OperationCanceledException) { }
        catch (RegexMatchTimeoutException) { StatusMessage = "[!] Regex search timed out. Use a simpler expression."; }
        catch (ArgumentException ex) { StatusMessage = $"[!] Invalid search: {ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"[!] Search failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void ReturnToLiveTail()
    {
        if (SelectedSession == null) return;
        _fullSearchCts?.Cancel();
        IsShowingFullFileSearch = false;
        var generation = Interlocked.Increment(ref _loadGeneration);
        _ = LoadSessionLogSafeAsync(SelectedSession, generation);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _deviceMonitor.DevicesChanged -= OnDevicesChanged;
        _deviceMonitor.DeviceConnected -= OnDeviceConnected;
        _deviceMonitor.DeviceDisconnected -= OnDeviceDisconnected;
        _sessionService.CaptureStarted -= OnCaptureStarted;
        _sessionService.CaptureStopped -= OnCaptureStopped;
        _crashDetector.CrashDetected -= OnCrashDetected;
        if (_isSubscribedToLogBatch)
        {
            _sessionService.LogBatchReceived -= OnLogBatchReceived;
            _isSubscribedToLogBatch = false;
        }
        try { _searchDebounceCts?.Cancel(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[Session] Dispose: cancel debounce CTS"); }
        try { _searchDebounceCts?.Dispose(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[Session] Dispose: dispose debounce CTS"); }
        _fullSearchCts?.Cancel();
        _fullSearchCts?.Dispose();
        if (IsScreenRecording)
            _ = StopScreenRecordAsync();
        GC.SuppressFinalize(this);
    }
}
