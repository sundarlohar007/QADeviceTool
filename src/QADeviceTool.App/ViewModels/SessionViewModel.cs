using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QADeviceTool.Models;
using QADeviceTool.Services;

namespace QADeviceTool.ViewModels;

/// <summary>
/// Sessions view — one-click capture, live log viewer with auto-scroll,
/// session-scoped snapshots, save logs, auto-capture on connect.
///
/// PERF: Log content is NOT data-bound. Instead, we fire events that
/// the View code-behind handles via TextBox.AppendText() at Background
/// priority so button clicks are never blocked.
/// </summary>
public partial class SessionViewModel : ObservableObject
{
    private readonly SessionService _sessionService;
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly DeviceMonitorService _deviceMonitor;
    private readonly Dispatcher _dispatcher;

    // ── Events for View code-behind (NOT data binding) ──
    /// <summary>Append text to the log viewer.</summary>
    public event Action<string>? LogAppend;
    /// <summary>Clear the log viewer.</summary>
    public event Action? LogCleared;
    /// <summary>Replace entire log viewer content.</summary>
    public event Action<string>? LogReplaced;

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
                    LogCleared?.Invoke();
                    LogAppend?.Invoke($"[{DateTime.Now:HH:mm:ss}] Device connected - auto-capture started for {device.DisplayName} ({device.Serial})\n");
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
                    LogAppend?.Invoke($"\n[{DateTime.Now:HH:mm:ss}] [!] Device disconnected - capture stopped.\n");
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
                LogCleared?.Invoke();
                LogAppend?.Invoke($"[{DateTime.Now:HH:mm:ss}] Capture started for {device.DisplayName} ({device.Serial})...\n");
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
        // Fire event — the View will AppendText at Background priority
        LogAppend?.Invoke(batch);
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
                LogAppend?.Invoke($"[{DateTime.Now:HH:mm:ss}] Snapshot saved: {fileName}\n");
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
            LogCleared?.Invoke();
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
        LogCleared?.Invoke();
    }

    partial void OnSelectedSessionChanged(LogSession? value)
    {
        if (value != null)
        {
            _ = LoadSessionLogSafe(value);
        }
        else
        {
            LogReplaced?.Invoke("Connect a device and click 'Start Capture' to begin.");
        }
    }

    private async Task LoadSessionLogSafe(LogSession session)
    {
        try
        {
            if (string.IsNullOrEmpty(session.LogFilePath) || !File.Exists(session.LogFilePath))
            {
                LogReplaced?.Invoke(session.Status == SessionStatus.Idle
                    ? "Ready to capture. Click 'Start' to begin."
                    : "No log file found.");
                return;
            }

            LogReplaced?.Invoke("Loading log...");
            var content = await _sessionService.ReadLogContentAsync(session);
            LogReplaced?.Invoke(content);
        }
        catch
        {
            LogReplaced?.Invoke("Could not load log file.");
        }
    }
}
