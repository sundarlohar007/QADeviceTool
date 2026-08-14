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
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly ConcurrentDictionary<string, CaptureContext> _activeCaptures = new();
    private System.Threading.Timer? _flushTimer;
    private readonly object _flushTimerLock = new();
    private readonly object _bufferLock = new();

    /// <summary>
    /// Fired with batched log lines (every 200ms) instead of per-line.
    /// The string key is the session Id so consumers can filter to their session.
    /// </summary>
    public event Action<string, string>? LogBatchReceived;

    public string SessionsRootDirectory { get; set; }

    public SessionService(IAdbService adbService, IIosService iosService)
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
        Process? process = session.Platform switch
        {
            DevicePlatform.Android => await _adbService.StartLogCaptureAsync(session.DeviceSerial, session.LogFilePath, buffer, format).ConfigureAwait(false),
            DevicePlatform.iOS => _iosService.StartLogCapture(session.DeviceSerial, session.LogFilePath),
            _ => null
        };

        if (process == null) return false;
        await Task.Delay(250).ConfigureAwait(false);
        if (process.HasExited)
        {
            try { process.Dispose(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Process dispose error"); }
            return false;
        }

        string targetPackageName = PreferencesService.Current.TargetPackageName;

        StreamWriter? writer = null;
        StreamWriter? appWriter = null;
        try
        {
            writer = new StreamWriter(session.LogFilePath, append: true);
            // FEAT-21: mark restarts so appended captures are distinguishable
            if (new FileInfo(session.LogFilePath) is { Length: > 0 })
            {
                writer.WriteLine("--- SESSION RESTARTED ---");
            }
            if (session.Platform == DevicePlatform.Android && !string.IsNullOrWhiteSpace(targetPackageName))
            {
                session.AppLogFilePath = Path.Combine(session.SessionDirectory, $"{session.Platform}_{session.DeviceId}_app_log.txt");
                appWriter = new StreamWriter(session.AppLogFilePath, append: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "Failed to create log writers");
            process.Kill(true);
            process.Dispose();
            writer?.Dispose();
            appWriter?.Dispose();
            return false;
        }

        var cts = new CancellationTokenSource();
        var ctx = new CaptureContext(process, writer, appWriter, session, cts, new ConcurrentQueue<string>());

        // TryAdd: if a capture for this session already exists, clean up and return false
        if (!_activeCaptures.TryAdd(session.Id, ctx))
        {
            process.Kill(session.Platform == DevicePlatform.iOS);
            process.Dispose();
            writer.Dispose();
            appWriter?.Dispose();
            cts.Dispose();
            return false;
        }

        session.Status = SessionStatus.Capturing;
        session.StartTime = DateTime.Now;

        // Start batched flush timer (200ms interval) — prevents UI flooding
        EnsureFlushTimer();

        // Periodic file flush (2s) — writes buffered log data to disk without blocking stdout reads
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000, cts.Token);
                try { await writer.FlushAsync(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Writer flush error"); }
                if (appWriter != null) { try { await appWriter.FlushAsync(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] AppWriter flush error"); } }
            }
        }, cts.Token);

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

        // Read output via OutputDataReceived — standard .NET async pattern, no pipe back-pressure
        var readComplete = new TaskCompletionSource<bool>();
        process.OutputDataReceived += (_, args) =>
        {
            try
            {
                if (args.Data == null)
                {
                    readComplete.TrySetResult(true);
                    return;
                }

                var line = args.Data;
                try { writer.WriteLine(line); } catch (Exception ex) { AppLogger.Log.Debug(ex, "Failed to write main log"); }

                if (appWriter != null && !string.IsNullOrWhiteSpace(currentTargetPid))
                {
                    if (Regex.IsMatch(line, $@"\b{Regex.Escape(currentTargetPid)}\b"))
                    {
                        try { appWriter.WriteLine(line); } catch (Exception ex) { AppLogger.Log.Debug(ex, "Failed to write app log"); }
                    }
                }

                session.LogLineCount++;
                ctx.Buffer.Enqueue(line);
            }
            catch (Exception ex)
            {
                AppLogger.Log.Error(ex, "Error processing log output line");
            }
        };
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();

        // When process exits, signal read complete
        process.Exited += (_, _) => readComplete.TrySetResult(true);

        AppLogger.Log.Info($"Capture started for device {session.DeviceId}");
        return true;
    }

    private void EnsureFlushTimer()
    {
        lock (_flushTimerLock)
        {
            _flushTimer?.Dispose();
            _flushTimer = new System.Threading.Timer(_ => FlushAllBuffers(), null, 200, 200);
        }
    }

    private void FlushAllBuffers()
    {
        if (!Monitor.TryEnter(_bufferLock, 0)) return;
        try
        {
            foreach (var kvp in _activeCaptures)
            {
                FlushCaptureBuffer(kvp.Key, kvp.Value);
            }
        }
        finally
        {
            Monitor.Exit(_bufferLock);
        }
    }

    private void FlushCaptureBuffer(string sessionId, CaptureContext ctx)
    {
        if (ctx.Buffer.IsEmpty) return;

        // Drain everything, fire in 2000-line chunks to keep UI batches manageable
        while (!ctx.Buffer.IsEmpty)
        {
            var batch = new System.Text.StringBuilder();
            int count = 0;
            while (ctx.Buffer.TryDequeue(out var line) && count < 2000)
            {
                batch.AppendLine(line);
                count++;
            }

            if (batch.Length > 0)
            {
                LogBatchReceived?.Invoke(sessionId, batch.ToString());
            }
        }
    }

    public void StopCapture(LogSession session)
    {
        if (!_activeCaptures.TryRemove(session.Id, out var ctx)) return;

        try
        {
            ctx.Cts.Cancel();

            if (!ctx.Process.HasExited)
            {
                bool killTree = ctx.Session.Platform == DevicePlatform.iOS;
                try { ctx.Process.Kill(killTree); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Kill error"); }
                try { ctx.Process.WaitForExit(1000); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] WaitForExit error"); }
            }

            try { ctx.Process.CancelOutputRead(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] CancelOutputRead error"); }
            try { ctx.Writer.Flush(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Writer flush error"); }
            try { ctx.AppWriter?.Flush(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] AppWriter flush error"); }
            FlushCaptureBuffer(session.Id, ctx);
            ctx.Writer.Dispose();
            ctx.AppWriter?.Dispose();
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] StopCapture cleanup error"); }
        finally
        {
            ctx.Process.Dispose();
            ctx.Cts.Dispose();
        }

        session.Status = SessionStatus.Stopped;
        session.EndTime = DateTime.Now;

        if (_activeCaptures.Count == 0)
        {
            lock (_flushTimerLock)
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
            }
        }

        AppLogger.Log.Info($"Capture stopped for device {session.DeviceId}. Duration: {session.EndTime - session.StartTime}");
    }

    public void StopAllCaptures()
    {
        foreach (var kvp in _activeCaptures.ToList())
        {
            if (!_activeCaptures.TryRemove(kvp.Key, out var ctx)) continue;
            try
            {
                ctx.Cts.Cancel();
                if (!ctx.Process.HasExited)
                {
                    bool killTree = ctx.Session.Platform == DevicePlatform.iOS;
                    try { ctx.Process.Kill(killTree); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Kill error"); }
                    try { ctx.Process.WaitForExit(1000); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] WaitForExit error"); }
                }
                try { ctx.Process.CancelOutputRead(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] CancelOutputRead error"); }
                try { ctx.Writer.Flush(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Writer flush error"); }
                try { ctx.AppWriter?.Flush(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] AppWriter flush error"); }
                FlushCaptureBuffer(kvp.Key, ctx);
                ctx.Writer.Dispose();
                ctx.AppWriter?.Dispose();
                ctx.Process.Dispose();
                ctx.Cts.Dispose();
            }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Error during StopAllCaptures cleanup"); }
        }
        lock (_flushTimerLock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
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

        // Tail-read via StreamReader — never load a 50-100MB+ file fully into memory (BUG-17).
        var lastLines = new Queue<string>(maxLines);
        using var reader = new StreamReader(session.LogFilePath);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (lastLines.Count == maxLines) lastLines.Dequeue();
            lastLines.Enqueue(line);
        }

        return string.Join(Environment.NewLine, lastLines);
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

    private record CaptureContext(Process Process, StreamWriter Writer, StreamWriter? AppWriter, LogSession Session, CancellationTokenSource Cts, ConcurrentQueue<string> Buffer);

    /// <summary>
    /// Exports session logs to CSV format.
    /// </summary>
    public async Task<bool> ExportToCsvAsync(LogSession session, string outputPath, bool anonymize = false)
    {
        try
        {
            if (!File.Exists(session.LogFilePath)) return false;

            using var reader = new StreamReader(session.LogFilePath);
            using var writer = new StreamWriter(outputPath, false);

            // CSV header
            await writer.WriteLineAsync("Timestamp,Level,Message");

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
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
    /// Exports session logs to JSON format. Streams via Utf8JsonWriter — never buffers the whole file.
    /// </summary>
    public async Task<bool> ExportToJsonAsync(LogSession session, string outputPath, bool anonymize = false)
    {
        try
        {
            if (!File.Exists(session.LogFilePath)) return false;

            using var reader = new StreamReader(session.LogFilePath);
            await using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var jsonWriter = new System.Text.Json.Utf8JsonWriter(outStream, new System.Text.Json.JsonWriterOptions { Indented = true });

            jsonWriter.WriteStartArray();

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parsed = ParseLogLine(line);
                var message = anonymize ? AnonymizeDeviceInfo(parsed["Message"]) : parsed["Message"];

                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("Timestamp", parsed["Timestamp"]);
                jsonWriter.WriteString("Level", parsed["Level"]);
                jsonWriter.WriteString("Message", message);
                jsonWriter.WriteEndObject();
            }

            jsonWriter.WriteEndArray();
            await jsonWriter.FlushAsync();
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
                        'F' => "Fatal",
                        'E' => "Error",
                        'W' => "Warning",
                        'I' => "Info",
                        'D' => "Debug",
                        'V' => "Verbose",
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
                        "F" => "Fatal",
                        "E" => "Error",
                        "W" => "Warning",
                        "I" => "Info",
                        "D" => "Debug",
                        "V" => "Verbose",
                        _ => "Unknown"
                    };
                }
            }
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] PreferencesService load failed, keeping defaults"); }

        return result;
    }

    private string AnonymizeDeviceInfo(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        var result = message;

        // Match known serial formats: Samsung RF..., Pixel HT..., generic alphanumeric, network host:port
        var serialPattern = new System.Text.RegularExpressions.Regex(@"\b(RF[A-Z0-9]{6,10}|HT[A-Z0-9]{6,10}|[A-Z]{2}[A-Z0-9]{6,14})\b");
        result = serialPattern.Replace(result, "[SERIAL]");

        var ipPattern = new System.Text.RegularExpressions.Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
        result = ipPattern.Replace(result, "[IP]");

        return result;
    }
}
