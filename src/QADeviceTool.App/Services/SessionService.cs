using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using QADeviceTool.Helpers;
using QADeviceTool.Models;

namespace QADeviceTool.Services;

/// <summary>
/// Manages log capture sessions — create, start, stop, save, and file I/O.
/// Uses batched log delivery to prevent UI thread flooding.
/// </summary>
public class SessionService
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly Dictionary<string, CaptureContext> _activeCaptures = new();
    private readonly ConcurrentQueue<string> _logBuffer = new();
    private System.Threading.Timer? _flushTimer;

    /// <summary>
    /// Fired with batched log lines (every 200ms) instead of per-line.
    /// </summary>
    public event Action<string>? LogBatchReceived;

    public string SessionsRootDirectory { get; set; }

    public SessionService(AdbService adbService, IosService iosService)
    {
        _adbService = adbService;
        _iosService = iosService;
        SessionsRootDirectory = PathHelper.GetDefaultSessionsDirectory();
        PathHelper.EnsureSessionsDirectory();
    }

    public LogSession CreateSession(DeviceInfo device)
    {
        var deviceLabel = !string.IsNullOrWhiteSpace(device.DisplayName) ? device.DisplayName : device.Serial;
        var sessionDir = PathHelper.CreateSessionDirectory(deviceLabel, SessionsRootDirectory);
        var logFileName = $"{device.Platform}_{device.Serial}_log.txt";
        var logFilePath = Path.Combine(sessionDir, logFileName);
        var folderName = System.IO.Path.GetFileName(sessionDir);

        return new LogSession
        {
            Name = folderName,
            DeviceId = device.Serial,
            DeviceName = device.DisplayName,
            Platform = device.Platform,
            LogFilePath = logFilePath,
            SessionDirectory = sessionDir,
            Status = SessionStatus.Idle
        };
    }

    /// <summary>
    /// Starts log capture for a session. Non-blocking.
    /// </summary>
    public bool StartCapture(LogSession session)
    {
        if (_activeCaptures.ContainsKey(session.Id)) return false;

        Process? process = session.Platform switch
        {
            DevicePlatform.Android => _adbService.StartLogCapture(session.DeviceId, session.LogFilePath),
            DevicePlatform.iOS => _iosService.StartLogCapture(session.DeviceId, session.LogFilePath),
            _ => null
        };

        if (process == null) return false;

        StreamWriter? writer = null;
        try
        {
            writer = new StreamWriter(session.LogFilePath, append: true) { AutoFlush = true };
        }
        catch
        {
            process.Kill(true);
            process.Dispose();
            return false;
        }

        var ctx = new CaptureContext(process, writer);
        _activeCaptures[session.Id] = ctx;

        session.Status = SessionStatus.Capturing;
        session.StartTime = DateTime.Now;

        // Start batched flush timer (200ms interval) — prevents UI flooding
        _flushTimer?.Dispose();
        _flushTimer = new System.Threading.Timer(_ => FlushLogBuffer(), null, 200, 200);

        // Read output asynchronously on a background thread
        Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line != null)
                    {
                        var timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
                        try { await writer.WriteLineAsync(timestamped); } catch { }
                        session.LogLineCount++;
                        _logBuffer.Enqueue(timestamped);
                    }
                }
            }
            catch { }
        });

        return true;
    }

    private void FlushLogBuffer()
    {
        if (_logBuffer.IsEmpty) return;

        var batch = new System.Text.StringBuilder();
        int count = 0;
        while (_logBuffer.TryDequeue(out var line) && count < 200)
        {
            batch.AppendLine(line);
            count++;
        }

        if (batch.Length > 0)
        {
            LogBatchReceived?.Invoke(batch.ToString());
        }
    }

    public void StopCapture(LogSession session)
    {
        if (!_activeCaptures.TryGetValue(session.Id, out var ctx)) return;

        try
        {
            // Close the writer first so no more log lines are written
            ctx.Writer.Dispose();

            // Gracefully terminate the logcat process without killing adb.exe server.
            // Closing StandardOutput causes the process to end on its own.
            try { ctx.Process.StandardOutput.Close(); } catch { }
            try { ctx.Process.StandardError.Close(); } catch { }

            // Give it a moment to exit, then force-kill only the child process as last resort
            if (!ctx.Process.HasExited)
            {
                try { ctx.Process.Kill(entireProcessTree: false); } catch { }
            }
        }
        catch { }
        finally
        {
            ctx.Process.Dispose();
            _activeCaptures.Remove(session.Id);
        }

        session.Status = SessionStatus.Stopped;
        session.EndTime = DateTime.Now;

        // Flush remaining lines
        FlushLogBuffer();

        // Stop flush timer if no more active captures
        if (_activeCaptures.Count == 0)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }

    public void StopAllCaptures()
    {
        foreach (var kvp in _activeCaptures.ToList())
        {
            try
            {
                kvp.Value.Writer.Dispose();
                try { kvp.Value.Process.StandardOutput.Close(); } catch { }
                try { kvp.Value.Process.StandardError.Close(); } catch { }
                if (!kvp.Value.Process.HasExited)
                {
                    try { kvp.Value.Process.Kill(entireProcessTree: false); } catch { }
                }
                kvp.Value.Process.Dispose();
            }
            catch { }
        }
        _activeCaptures.Clear();
        _flushTimer?.Dispose();
        _flushTimer = null;
    }

    /// <summary>
    /// Saves the current in-memory log content to a file.
    /// </summary>
    public async Task<string> SaveLogToFileAsync(LogSession session, string logContent)
    {
        try
        {
            var dir = session.SessionDirectory;
            if (string.IsNullOrEmpty(dir))
                dir = SessionsRootDirectory;

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var filePath = string.IsNullOrEmpty(session.LogFilePath)
                ? Path.Combine(dir, $"manual_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt")
                : session.LogFilePath;

            await File.WriteAllTextAsync(filePath, logContent);
            return filePath;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public List<LogSession> GetSavedSessions()
    {
        var sessions = new List<LogSession>();
        if (!Directory.Exists(SessionsRootDirectory)) return sessions;

        foreach (var dir in Directory.GetDirectories(SessionsRootDirectory).OrderByDescending(d => d))
        {
            var dirName = Path.GetFileName(dir);
            var logFiles = Directory.GetFiles(dir, "*.txt").Concat(Directory.GetFiles(dir, "*.log")).ToArray();

            var session = new LogSession
            {
                Name = dirName,
                SessionDirectory = dir,
                Status = SessionStatus.Stopped,
                StartTime = Directory.GetCreationTime(dir)
            };

            if (logFiles.Length > 0)
            {
                session.LogFilePath = logFiles[0];
                var fi = new FileInfo(logFiles[0]);
                session.EndTime = fi.LastWriteTime;
            }

            sessions.Add(session);
        }

        return sessions;
    }

    public async Task<string> ReadLogContentAsync(LogSession session, int maxLines = 1000)
    {
        if (string.IsNullOrEmpty(session.LogFilePath) || !File.Exists(session.LogFilePath))
            return "No log file found.";

        var lines = await File.ReadAllLinesAsync(session.LogFilePath);
        var subset = lines.TakeLast(maxLines).ToArray();
        return string.Join(Environment.NewLine, subset);
    }

    public bool DeleteSession(LogSession session)
    {
        try
        {
            if (Directory.Exists(session.SessionDirectory))
            {
                Directory.Delete(session.SessionDirectory, true);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Whether any capture is currently active.
    /// </summary>
    public bool HasActiveCapture => _activeCaptures.Count > 0;

    /// <summary>
    /// Returns the session ID currently capturing for the given device serial, or null.
    /// </summary>
    public string? GetActiveSessionIdForDevice(string deviceSerial)
    {
        // LogSession stores DeviceId = serial
        return _activeCaptures.Keys.FirstOrDefault();
    }

    /// <summary>
    /// Stops capture for any active session that belongs to the given device serial.
    /// Returns the stopped session, or null if none was active for that device.
    /// </summary>
    public LogSession? StopCaptureForDevice(string deviceSerial, IEnumerable<LogSession> sessions)
    {
        var session = sessions.FirstOrDefault(s =>
            s.DeviceId == deviceSerial && s.Status == SessionStatus.Capturing);

        if (session != null)
            StopCapture(session);

        return session;
    }

    private record CaptureContext(Process Process, StreamWriter Writer);
}
