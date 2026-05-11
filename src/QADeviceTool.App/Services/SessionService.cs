using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Manages log capture sessions — create, start, stop, save, and file I/O.
/// Uses batched log delivery to prevent UI thread flooding.
/// </summary>
public class SessionService : ISessionService
{
    private readonly AdbService _adbService;
    private readonly IosService _iosService;
    private readonly ConcurrentDictionary<string, CaptureContext> _activeCaptures = new();
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
        SessionsRootDirectory = PreferencesService.Current.SessionsRootDirectory;
        if (!Directory.Exists(SessionsRootDirectory)) Directory.CreateDirectory(SessionsRootDirectory);
    }

    public LogSession CreateSession(DeviceInfo device, string? customSessionName = null)
    {
        var deviceHash = SecurityHelper.HashSerial(device.Serial);
        var sessionName = SecurityHelper.GetSafeSessionName(customSessionName, deviceHash, device.Platform.ToString());
        
        var sessionDir = PathHelper.CreateSessionDirectory(sessionName, SessionsRootDirectory);
        var logFileName = $"{sessionName}_log.txt";
        var logFilePath = Path.Combine(sessionDir, logFileName);
        var folderName = System.IO.Path.GetFileName(sessionDir);

        return new LogSession
        {
            Name = sessionName,
            DeviceId = deviceHash,
            DeviceSerial = device.Serial,
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
    public async Task<bool> StartCaptureAsync(LogSession session, LogcatBuffer buffer = LogcatBuffer.Main, LogcatFormat format = LogcatFormat.ThreadTime)
    {
        if (!_activeCaptures.TryAdd(session.Id, null!)) return false;

        Process? process = session.Platform switch
        {
            DevicePlatform.Android => await _adbService.StartLogCaptureAsync(session.DeviceSerial, session.LogFilePath, buffer, format).ConfigureAwait(false),
            DevicePlatform.iOS => _iosService.StartLogCapture(session.DeviceSerial, session.LogFilePath),
            _ => null
        };

        if (process == null) { _activeCaptures.TryRemove(session.Id, out _); return false; }

        string targetPackageName = PreferencesService.Current.TargetPackageName;

        StreamWriter? writer = null;
        StreamWriter? appWriter = null;
        try
        {
            writer = new StreamWriter(session.LogFilePath, append: true) { AutoFlush = true };
            if (session.Platform == DevicePlatform.Android && !string.IsNullOrWhiteSpace(targetPackageName))
            {
                session.AppLogFilePath = Path.Combine(session.SessionDirectory, $"{session.Platform}_{session.DeviceId}_app_log.txt");
                appWriter = new StreamWriter(session.AppLogFilePath, append: true) { AutoFlush = true };
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "Failed to create log writers");
            process.Kill(true);
            process.Dispose();
            writer?.Dispose();
            appWriter?.Dispose();
            _activeCaptures.TryRemove(session.Id, out _);
            return false;
        }

        var cts = new CancellationTokenSource();
        var ctx = new CaptureContext(process, writer, appWriter, session, cts);
        _activeCaptures.TryUpdate(session.Id, ctx, null!);

        session.Status = SessionStatus.Capturing;
        session.StartTime = DateTime.Now;

        // Start batched flush timer (200ms interval) — prevents UI flooding
        _flushTimer?.Dispose();
        _flushTimer = new System.Threading.Timer(_ => FlushLogBuffer(), null, 200, 200);

        string currentTargetPid = string.Empty;
        if (appWriter != null && !string.IsNullOrWhiteSpace(targetPackageName))
        {
            Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var pid = await _adbService.GetPidFromPackageNameAsync(session.DeviceSerial, targetPackageName);
                        if (!string.IsNullOrWhiteSpace(pid) && currentTargetPid != pid)
                        {
                            currentTargetPid = pid;
                            // Write PID resolution notice only to app-specific log, NOT to main log buffer
                            // Main log stays pure device output
                            var notice = $"[{DateTime.Now:HH:mm:ss.fff}] PID:{targetPackageName}={pid}";
                            try { await appWriter.WriteLineAsync(notice); } catch (Exception ex) { AppLogger.Log.Debug(ex, "Failed to write app log notice"); }
                        }
                    }
                    catch (Exception ex) { AppLogger.Log.Debug(ex, "Failed to resolve package PID"); }
                    await Task.Delay(3000, cts.Token);
                }
            }, cts.Token);
        }

        // Read output asynchronously on a background thread
        Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? line;
                    try { line = await process.StandardOutput.ReadLineAsync(); }
                    catch (InvalidOperationException) { break; } // process exited/disposed

                    if (line == null) break; // end of stream

                    try { await writer.WriteLineAsync(line); } catch (Exception ex) { AppLogger.Log.Debug(ex, "Failed to write main log"); }

                    if (appWriter != null && !string.IsNullOrWhiteSpace(currentTargetPid))
                    {
                        if (Regex.IsMatch(line, $@"\b{currentTargetPid}\b"))
                        {
                            try { await appWriter.WriteLineAsync(line); } catch (Exception ex) { AppLogger.Log.Debug(ex, "Failed to write app log"); }
                        }
                    }

                    session.LogLineCount++;
                    _logBuffer.Enqueue(line);
                }
            }
            catch (Exception ex) { AppLogger.Log.Error(ex, "Error reading log output"); }
        }, cts.Token);

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
            ctx.Cts.Cancel();
            // Close the writer first so no more log lines are written
            ctx.Writer.Dispose();
            ctx.AppWriter?.Dispose();

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
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] StopCapture cleanup error"); }
        finally
        {
            ctx.Process.Dispose();
            ctx.Cts.Dispose();
            _activeCaptures.TryRemove(session.Id, out _);
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
                kvp.Value.Cts.Cancel();
                kvp.Value.Writer.Dispose();
                kvp.Value.AppWriter?.Dispose();
                try { kvp.Value.Process.StandardOutput.Close(); } catch { }
                try { kvp.Value.Process.StandardError.Close(); } catch { }
                if (!kvp.Value.Process.HasExited)
                {
                    try { kvp.Value.Process.Kill(entireProcessTree: false); } catch { }
                }
                kvp.Value.Process.Dispose();
                kvp.Value.Cts.Dispose();
            }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Error during StopAllCaptures cleanup"); }
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

            await File.WriteAllTextAsync(filePath, logContent).ConfigureAwait(false);
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

    public async Task<string> ReadLogContentAsync(LogSession session, int maxLines = 200000)
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
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] DeleteSession failed"); }
        return false;
    }

    /// <summary>
    /// Whether any capture is currently active.
    /// </summary>
    public bool HasActiveCapture => _activeCaptures.Count > 0;

    public LogSession? GetActiveSessionForDevice(string deviceSerial)
    {
        return _activeCaptures.Values
            .Where(ctx => ctx.Session.DeviceSerial == deviceSerial)
            .Select(ctx => ctx.Session)
            .FirstOrDefault();
    }

    /// <summary>
    /// Stops capture for any active session that belongs to the given device serial.
    /// Returns the stopped session, or null if none was active for that device.
    /// </summary>
    public LogSession? StopCaptureForDevice(string deviceSerial, IEnumerable<LogSession> sessions)
    {
        var session = sessions.FirstOrDefault(s =>
            s.DeviceSerial == deviceSerial && s.Status == SessionStatus.Capturing);

        if (session != null)
            StopCapture(session);

        return session;
    }

    private record CaptureContext(Process Process, StreamWriter Writer, StreamWriter? AppWriter, LogSession Session, CancellationTokenSource Cts);

    /// <summary>
    /// Exports session logs to CSV format.
    /// </summary>
    public async Task<bool> ExportToCsvAsync(LogSession session, string outputPath, bool anonymize = false)
    {
        try
        {
            if (!File.Exists(session.LogFilePath)) return false;
            
            var lines = await File.ReadAllLinesAsync(session.LogFilePath);
            using var writer = new StreamWriter(outputPath, false);
            
            // CSV header
            await writer.WriteLineAsync("Timestamp,Level,Message");
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                var parsed = ParseLogLine(line);
                var message = parsed["Message"];
                
                if (anonymize)
                {
                    message = AnonymizeDeviceInfo(message);
                }
                
                var escapedMessage = message.Replace("\"", "\"\"");
                await writer.WriteLineAsync($"\"{parsed["Timestamp"]}\",\"{parsed["Level"]}\",\"{escapedMessage}\"");
            }
            
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "Failed to export session to CSV");
            return false;
        }
    }

    /// <summary>
    /// Exports session logs to JSON format.
    /// </summary>
    public async Task<bool> ExportToJsonAsync(LogSession session, string outputPath, bool anonymize = false)
    {
        try
        {
            if (!File.Exists(session.LogFilePath)) return false;
            
            var lines = await File.ReadAllLinesAsync(session.LogFilePath);
            var entries = new List<Dictionary<string, string>>();
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                var parsed = ParseLogLine(line);
                
                if (anonymize)
                {
                    parsed["Message"] = AnonymizeDeviceInfo(parsed["Message"]);
                }
                
                entries.Add(parsed);
            }
            
            var json = System.Text.Json.JsonSerializer.Serialize(entries, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            await File.WriteAllTextAsync(outputPath, json);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "Failed to export session to JSON");
            return false;
        }
    }

    private static Dictionary<string, string> ParseLogLine(string line)
    {
        var result = new Dictionary<string, string>
        {
            { "Timestamp", "" },
            { "Level", "Unknown" },
            { "Message", line }
        };

        try
        {
            // Format 1: Standard logcat -v threadtime
            // "MM-DD HH:MM:SS.mmm   PID  TID P/Tag: message"
            if (line.Length > 30 && line[2] == '-' && line[5] == ' ' && line[19] == '.')
            {
                result["Timestamp"] = line.Substring(0, 18);
                var rest = line.Substring(30).TrimStart();
                // Extract level from P/Tag prefix
                if (rest.Length >= 2 && rest[1] == '/')
                {
                    result["Level"] = rest[0] switch
                    {
                        'F' => "Fatal", 'E' => "Error", 'W' => "Warning",
                        'I' => "Info", 'D' => "Debug", 'V' => "Verbose",
                        _ => "Unknown"
                    };
                }
                result["Message"] = rest;
            }
            // Format 2: Legacy bracket format "[HH:mm:ss.fff] E/Tag: message"
            else if (line.StartsWith("["))
            {
                var closeBracket = line.IndexOf(']');
                if (closeBracket > 1)
                {
                    result["Timestamp"] = line.Substring(1, closeBracket - 1);
                    var rest = line.Substring(closeBracket + 1).TrimStart();
                    result["Message"] = rest;

                    if (rest.StartsWith("F/")) result["Level"] = "Fatal";
                    else if (rest.StartsWith("E/")) result["Level"] = "Error";
                    else if (rest.StartsWith("W/")) result["Level"] = "Warning";
                    else if (rest.StartsWith("D/")) result["Level"] = "Debug";
                    else if (rest.StartsWith("I/")) result["Level"] = "Info";
                    else if (rest.StartsWith("V/")) result["Level"] = "Verbose";
                }
            }
            // Format 3: Fallback — try P/ prefix anywhere in the line
            else
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"\b([FEWIDV])/");
                if (match.Success)
                {
                    result["Level"] = match.Groups[1].Value switch
                    {
                        "F" => "Fatal", "E" => "Error", "W" => "Warning",
                        "I" => "Info", "D" => "Debug", "V" => "Verbose",
                        _ => "Unknown"
                    };
                }
            }
        }
        catch { /* keep defaults */ }

        return result;
    }

    private string AnonymizeDeviceInfo(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        var result = message;

        var serialPattern = new System.Text.RegularExpressions.Regex(@"\b[A-Z0-9]{8,20}\b");
        result = serialPattern.Replace(result, "[SERIAL]");

        var ipPattern = new System.Text.RegularExpressions.Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
        result = ipPattern.Replace(result, "[IP]");

        return result;
    }
}
