using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

public readonly record struct CaptureStatistics(int PendingLines, long DroppedLines);

/// <summary>
/// Manages log capture sessions — create, start, stop, save, and file I/O.
/// Uses batched log delivery to prevent UI thread flooding.
/// </summary>
public class SessionService : ISessionService
{
    private readonly IAdbService _adbService;
    private readonly IIosService _iosService;
    private readonly ConcurrentDictionary<string, CaptureContext> _activeCaptures = new();
    private readonly ConcurrentDictionary<string, byte> _startingDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _stopTasks = new();
    private readonly ConcurrentDictionary<string, Task> _captureStopTasks = new();
    private System.Threading.Timer? _flushTimer;
    private readonly object _flushTimerLock = new();
    private readonly object _bufferLock = new();
    private string _sessionsRootDirectory;

    /// <summary>
    /// Fired with batched log lines (every 200ms) instead of per-line.
    /// The string key is the session Id so consumers can filter to their session.
    /// </summary>
    public event Action<string, string>? LogBatchReceived;
    public event Action<LogSession>? CaptureStarted;
    public event Action<LogSession>? CaptureStopped;

    public IReadOnlyList<LogSession> ActiveSessions => _activeCaptures.Values.Select(ctx => ctx.Session).ToList();
    public CaptureStatistics GetCaptureStatistics(string sessionId) =>
        _activeCaptures.TryGetValue(sessionId, out var capture)
            ? new CaptureStatistics(capture.Buffer.Count, Interlocked.Read(ref capture.DroppedLines))
            : default;

    public string SessionsRootDirectory
    {
        get => _sessionsRootDirectory;
        set
        {
            if (!PathHelper.TryGetSafeLocalDirectory(value, out var safePath))
            {
                AppLogger.Log.Warn("[SessionService] Rejected non-local or reparse-point sessions path; keeping current path.");
                return;
            }

            _sessionsRootDirectory = safePath;
            PathHelper.RestrictDirectoryAccess(_sessionsRootDirectory);
        }
    }

    public SessionService(IAdbService adbService, IIosService iosService)
    {
        _adbService = adbService;
        _iosService = iosService;
        _sessionsRootDirectory = PathHelper.GetDefaultSessionsDirectory();
        SessionsRootDirectory = PreferencesService.Current.SessionsRootDirectory;
    }

    public LogSession CreateSession(DeviceInfo device, string? customSessionName = null)
    {
        var deviceHash = SecurityHelper.HashSerial(device.Serial);
        var sessionName = SecurityHelper.GetSafeSessionName(customSessionName, deviceHash, device.Platform.ToString());

        var sessionDir = PathHelper.CreateSessionDirectory(sessionName, SessionsRootDirectory);
        var logFileName = $"{sessionName}_log.txt";
        var logFilePath = Path.Combine(sessionDir, logFileName);
        var folderName = System.IO.Path.GetFileName(sessionDir);

        var session = new LogSession
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
        SaveSessionMetadata(session);
        return session;
    }

    /// <summary>
    /// Starts log capture for a session. Non-blocking.
    /// </summary>
    public async Task<bool> StartCaptureAsync(LogSession session, LogcatBuffer buffer = LogcatBuffer.Main, LogcatFormat format = LogcatFormat.ThreadTime)
    {
        if (_activeCaptures.Values.Any(ctx => ctx.Session.DeviceSerial.Equals(session.DeviceSerial, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!_startingDevices.TryAdd(session.DeviceSerial, 0)) return false;

        Process? process;
        try
        {
            process = session.Platform switch
            {
                DevicePlatform.Android => await _adbService.StartLogCaptureAsync(session.DeviceSerial, session.LogFilePath, buffer, format).ConfigureAwait(false),
                DevicePlatform.iOS => await _iosService.StartLogCaptureAsync(session.DeviceSerial, session.LogFilePath).ConfigureAwait(false),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _startingDevices.TryRemove(session.DeviceSerial, out _);
            AppLogger.Log.Error(ex, "[SessionService] Failed to start capture process");
            return false;
        }

        if (process == null)
        {
            _startingDevices.TryRemove(session.DeviceSerial, out _);
            return false;
        }
        await Task.Delay(250).ConfigureAwait(false);
        if (process.HasExited)
        {
            try { process.Dispose(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Process dispose error"); }
            _startingDevices.TryRemove(session.DeviceSerial, out _);
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
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            writer?.Dispose();
            appWriter?.Dispose();
            _startingDevices.TryRemove(session.DeviceSerial, out _);
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
            _startingDevices.TryRemove(session.DeviceSerial, out _);
            return false;
        }
        _startingDevices.TryRemove(session.DeviceSerial, out _);

        session.Status = SessionStatus.Capturing;
        session.StartTime = DateTime.Now;
        SaveSessionMetadata(session);

        // Start batched flush timer (200ms interval) — prevents UI flooding
        EnsureFlushTimer();

        // Periodic file flush (2s) — writes buffered log data to disk without blocking stdout reads
        ctx.FlushTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(2000, cts.Token).ConfigureAwait(false);
                    try { lock (ctx.WriterLock) { if (!ctx.WritersClosed) { writer.Flush(); appWriter?.Flush(); } } }
                    catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Writer flush error"); }
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        string currentTargetPid = string.Empty;
        if (appWriter != null && !string.IsNullOrWhiteSpace(targetPackageName))
        {
            ctx.PidTask = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var pid = await _adbService.GetPidFromPackageNameAsync(session.DeviceSerial, targetPackageName).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(pid) && currentTargetPid != pid)
                            {
                                currentTargetPid = pid;
                                // Write PID resolution notice only to app-specific log, NOT to main log buffer.
                                var notice = $"[{DateTime.Now:HH:mm:ss.fff}] PID:{targetPackageName}={pid}";
                                try { lock (ctx.WriterLock) { if (!ctx.WritersClosed) appWriter.WriteLine(notice); } }
                                catch (Exception ex) { AppLogger.Log.Debug(ex, "Failed to write app log notice"); }
                            }
                        }
                        catch (Exception ex) { AppLogger.Log.Debug(ex, "Failed to resolve package PID"); }
                        await Task.Delay(3000, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);
        }

        // Read output via OutputDataReceived — standard .NET async pattern, no pipe back-pressure
        process.OutputDataReceived += (_, args) =>
        {
            try
            {
                if (args.Data == null)
                {
                    ctx.OutputCompleted.TrySetResult(true);
                    return;
                }

                var line = args.Data;
                lock (ctx.WriterLock)
                {
                    if (!ctx.WritersClosed)
                    {
                        writer.WriteLine(line);
                        if (appWriter != null && !string.IsNullOrWhiteSpace(currentTargetPid) &&
                            Regex.IsMatch(line, $@"\b{Regex.Escape(currentTargetPid)}\b"))
                            appWriter.WriteLine(line);
                    }
                }

                session.LogLineCount++;
                ctx.Buffer.Enqueue(line);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref ctx.DroppedLines);
                AppLogger.Log.Error(ex, "Error processing log output line");
            }
        };
        PublishCaptureEvent(CaptureStarted, session);
        // Attach the exit handler before enabling events so a fast tool/device disconnect
        // cannot leave a capture permanently marked as active.
        process.Exited += (_, _) =>
        {
            _ = Task.Run(() => StopCapture(session));
        };
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();

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
                var handlers = LogBatchReceived;
                if (handlers == null) continue;
                var batchText = batch.ToString();
                foreach (Action<string, string> handler in handlers.GetInvocationList())
                {
                    try { handler(sessionId, batchText); }
                    catch (Exception ex) { AppLogger.Log.Warn(ex, "[SessionService] Log observer failed"); }
                }
            }
        }
    }

    public void StopCapture(LogSession session)
    {
        if (!_activeCaptures.TryRemove(session.Id, out var ctx)) return;

        var stopTask = Task.Run(async () =>
        {
            try
            {
                ctx.Cts.Cancel();

                if (!ctx.Process.HasExited)
                {
                    bool killTree = ctx.Session.Platform == DevicePlatform.iOS;
                    try { ctx.Process.Kill(killTree); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Kill error"); }
                    try { ctx.Process.WaitForExit(1000); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] WaitForExit error"); }
                }

                try { await Task.WhenAny(ctx.OutputCompleted.Task, Task.Delay(2000)).ConfigureAwait(false); } catch { }
                if (ctx.FlushTask != null) try { await Task.WhenAny(ctx.FlushTask, Task.Delay(2000)).ConfigureAwait(false); } catch { }
                if (ctx.PidTask != null) try { await Task.WhenAny(ctx.PidTask, Task.Delay(2000)).ConfigureAwait(false); } catch { }
                lock (ctx.WriterLock)
                {
                    ctx.WritersClosed = true;
                    try { ctx.Writer.Flush(); ctx.AppWriter?.Flush(); }
                    catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Writer flush error"); }
                    ctx.Writer.Dispose();
                    ctx.AppWriter?.Dispose();
                }
                FlushCaptureBuffer(session.Id, ctx);
            }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] StopCapture cleanup error"); }
            finally
            {
                _startingDevices.TryRemove(session.DeviceSerial, out _);
                ctx.Process.Dispose();
                ctx.Cts.Dispose();
            }
        });
        lock (_stopTasks)
        {
            _stopTasks.RemoveAll(t => t.IsCompleted);
            _stopTasks.Add(stopTask);
        }
        _captureStopTasks[session.Id] = stopTask;

        session.Status = SessionStatus.Stopped;
        session.EndTime = DateTime.Now;
        SaveSessionMetadata(session);
        PublishCaptureEvent(CaptureStopped, session);

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
        foreach (var session in _activeCaptures.Values.Select(c => c.Session).ToList())
            StopCapture(session);

        foreach (var serial in _startingDevices.Keys)
            _startingDevices.TryRemove(serial, out _);

        lock (_flushTimerLock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        Task[] pendingStops;
        lock (_stopTasks) pendingStops = _stopTasks.ToArray();
        try
        {
            if (!Task.WhenAll(pendingStops).Wait(TimeSpan.FromSeconds(5)))
                AppLogger.Log.Warn("[SessionService] Capture shutdown timed out before every file closed");
        }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[SessionService] Capture shutdown incomplete"); }
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

            if (!PathHelper.TryGetSafeLocalDirectory(dir, out var safeDir))
                return "Error: output directory must be local and non-reparse-point.";

            var filePath = Path.Combine(safeDir, $"manual_log_{Guid.NewGuid():N}.txt");
            if (!PathHelper.IsSafeLocalPath(filePath))
                return "Error: output file must be local and non-reparse-point.";

            await File.WriteAllTextAsync(filePath, logContent).ConfigureAwait(false);
            return filePath;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> SaveLogCopyAsync(LogSession session)
    {
        string? destination = null;
        try
        {
            await AwaitCaptureStopAsync(session.Id).ConfigureAwait(false);
            if (!PathHelper.IsSafeLocalPath(session.LogFilePath) || !File.Exists(session.LogFilePath) ||
                !PathHelper.TryGetSafeLocalDirectory(session.SessionDirectory, out var directory))
                return "Error: log file or session directory is unavailable.";
            if (_activeCaptures.TryGetValue(session.Id, out var capture))
            {
                lock (capture.WriterLock)
                    if (!capture.WritersClosed) capture.Writer.Flush();
            }
            destination = Path.Combine(directory, $"saved_log_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.txt");
            await using var source = new FileStream(session.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous);
            var remaining = source.Length;
            var buffer = new byte[64 * 1024];
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)))
                    .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Capture file shrank during save.");
                await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                remaining -= read;
            }
            return destination;
        }
        catch (Exception ex)
        {
            try { if (destination != null && File.Exists(destination)) File.Delete(destination); }
            catch (Exception cleanupError) { AppLogger.Log.Debug(cleanupError, "[SessionService] Partial save cleanup failed"); }
            return $"Error: {ex.Message}";
        }
    }

    private async Task AwaitCaptureStopAsync(string sessionId)
    {
        if (_captureStopTasks.TryGetValue(sessionId, out var task))
        {
            await task.ConfigureAwait(false);
            _captureStopTasks.TryRemove(sessionId, out _);
        }
    }

    private const string MetadataFileName = "session.json";
    private sealed record SessionMetadata(string Id, string Name, string DeviceId, string DeviceName,
        DevicePlatform Platform, string LogFileName, string AppLogFileName, DateTime StartTime, DateTime? EndTime);

    private static void SaveSessionMetadata(LogSession session)
    {
        string? pendingPath = null;
        try
        {
            if (!PathHelper.IsSafeLocalPath(session.SessionDirectory)) return;
            var metadata = new SessionMetadata(session.Id, session.Name, session.DeviceId,
                SecurityHelper.RedactSensitiveText(session.DeviceName), session.Platform,
                Path.GetFileName(session.LogFilePath), Path.GetFileName(session.AppLogFilePath), session.StartTime, session.EndTime);
            pendingPath = Path.Combine(session.SessionDirectory, $".session_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(pendingPath, JsonSerializer.Serialize(metadata));
            File.Move(pendingPath, Path.Combine(session.SessionDirectory, MetadataFileName), overwrite: true);
        }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[SessionService] Failed to persist session metadata"); }
        finally
        {
            try { if (pendingPath != null && File.Exists(pendingPath)) File.Delete(pendingPath); }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Metadata temp cleanup failed"); }
        }
    }

    public List<LogSession> GetSavedSessions()
    {
        var sessions = new List<LogSession>();
        if (!Directory.Exists(SessionsRootDirectory)) return sessions;

        foreach (var dir in Directory.GetDirectories(SessionsRootDirectory).OrderByDescending(d => d))
        {
            try
            {
                if (!PathHelper.IsSafeLocalPath(dir)) continue;
                var dirName = Path.GetFileName(dir);
                var logFiles = Directory.GetFiles(dir, "*_log.txt")
                    .Where(f => !f.EndsWith("_app_log.txt", StringComparison.OrdinalIgnoreCase))
                    .Concat(Directory.GetFiles(dir, "*.log"))
                    .Concat(Directory.GetFiles(dir, "manual_log_*.txt"))
                    .Concat(Directory.GetFiles(dir, "saved_log_*.txt"))
                    .ToArray();

                var session = new LogSession
                {
                    Name = dirName,
                    SessionDirectory = dir,
                    Status = SessionStatus.Stopped,
                    StartTime = Directory.GetCreationTime(dir)
                };

                var metadataPath = Path.Combine(dir, MetadataFileName);
                if (File.Exists(metadataPath))
                {
                    try
                    {
                        var metadata = JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllText(metadataPath));
                        if (metadata != null)
                        {
                            session.Id = metadata.Id;
                            session.Name = metadata.Name;
                            session.DeviceId = metadata.DeviceId;
                            session.DeviceName = metadata.DeviceName;
                            session.Platform = metadata.Platform;
                            session.StartTime = metadata.StartTime;
                            session.EndTime = metadata.EndTime;
                            if (Path.GetFileName(metadata.LogFileName) == metadata.LogFileName)
                            {
                                var candidate = Path.Combine(dir, metadata.LogFileName);
                                if (File.Exists(candidate) && PathHelper.IsSafeLocalPath(candidate)) session.LogFilePath = candidate;
                            }
                            if (Path.GetFileName(metadata.AppLogFileName) == metadata.AppLogFileName)
                            {
                                var appCandidate = Path.Combine(dir, metadata.AppLogFileName);
                                if (File.Exists(appCandidate) && PathHelper.IsSafeLocalPath(appCandidate)) session.AppLogFilePath = appCandidate;
                            }
                        }
                    }
                    catch (Exception ex) { AppLogger.Log.Warn(ex, "[SessionService] Invalid session metadata"); }
                }
                if (string.IsNullOrEmpty(session.LogFilePath) && logFiles.Length > 0)
                {
                    session.LogFilePath = logFiles[0];
                }
                if (File.Exists(session.LogFilePath) && session.EndTime == null)
                    session.EndTime = File.GetLastWriteTime(session.LogFilePath);

                sessions.Add(session);
            }
            catch (Exception ex) { AppLogger.Log.Warn(ex, "[SessionService] Could not load saved session"); }
        }

        return sessions;
    }

    public async Task<string> ReadLogContentAsync(LogSession session, int maxLines = 200000)
    {
        await AwaitCaptureStopAsync(session.Id).ConfigureAwait(false);
        if (string.IsNullOrEmpty(session.LogFilePath) || !File.Exists(session.LogFilePath) ||
            !PathHelper.IsSafeLocalPath(session.LogFilePath))
            return "No log file found.";
        if (maxLines <= 0) return string.Empty;

        // Find the last requested line boundary from the end. A small tail request must
        // not scan every line of a large capture just to discard almost all of them.
        using var stream = new FileStream(session.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        var position = stream.Length;
        var start = 0L;
        var separators = 0;
        var trailingLineEnding = true;
        while (position > 0 && separators < maxLines)
        {
            var chunkSize = (int)Math.Min(buffer.Length, position);
            var chunkStart = position - chunkSize;
            stream.Position = chunkStart;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, chunkSize)).ConfigureAwait(false);
            for (var i = chunkSize - 1; i >= 0; i--)
            {
                var current = buffer[i];
                if (trailingLineEnding && current is (byte)'\r' or (byte)'\n') continue;
                trailingLineEnding = false;
                if (current != (byte)'\n' || ++separators != maxLines) continue;
                start = chunkStart + i + 1;
                break;
            }
            position = chunkStart;
        }

        stream.Position = start;
        var lastLines = new Queue<string>(maxLines);
        using var reader = new StreamReader(stream);
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
            if (_captureStopTasks.TryGetValue(session.Id, out var stopTask))
            {
                if (!stopTask.Wait(TimeSpan.FromSeconds(5))) return false;
                _captureStopTasks.TryRemove(session.Id, out _);
            }
            if (!PathHelper.IsSafeLocalPath(session.SessionDirectory)) return false;
            var root = Path.GetFullPath(SessionsRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(session.SessionDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
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

    private static void PublishCaptureEvent(Action<LogSession>? handlers, LogSession session)
    {
        if (handlers == null) return;
        foreach (Action<LogSession> handler in handlers.GetInvocationList())
        {
            try { handler(session); }
            catch (Exception ex) { AppLogger.Log.Warn(ex, "[SessionService] Capture observer failed"); }
        }
    }

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

    private sealed class CaptureContext
    {
        public CaptureContext(Process process, StreamWriter writer, StreamWriter? appWriter, LogSession session,
            CancellationTokenSource cts, ConcurrentQueue<string> buffer)
        {
            Process = process;
            Writer = writer;
            AppWriter = appWriter;
            Session = session;
            Cts = cts;
            Buffer = buffer;
        }

        public Process Process { get; }
        public StreamWriter Writer { get; }
        public StreamWriter? AppWriter { get; }
        public LogSession Session { get; }
        public CancellationTokenSource Cts { get; }
        public ConcurrentQueue<string> Buffer { get; }
        public TaskCompletionSource<bool> OutputCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? FlushTask { get; set; }
        public Task? PidTask { get; set; }
        public object WriterLock { get; } = new();
        public bool WritersClosed { get; set; }
        public long DroppedLines;
    }

    /// <summary>
    /// Exports session logs to CSV format.
    /// </summary>
    public async Task<bool> ExportToCsvAsync(LogSession session, string outputPath, bool anonymize = true)
    {
        try
        {
            await AwaitCaptureStopAsync(session.Id).ConfigureAwait(false);
            if (!File.Exists(session.LogFilePath) || !PathHelper.IsSafeLocalPath(session.LogFilePath) ||
                !PathHelper.IsSafeLocalPath(outputPath) ||
                Path.GetFullPath(session.LogFilePath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)) return false;

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

                // Raw exports are never produced by the product. Keep the parameter for
                // source compatibility, but always redact sensitive device/game data.
                message = SecurityHelper.RedactSensitiveText(message);

                await writer.WriteLineAsync(string.Join(',',
                    EscapeCsvCell(parsed["Timestamp"]),
                    EscapeCsvCell(parsed["Level"]),
                    EscapeCsvCell(message)));
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
    public async Task<bool> ExportToJsonAsync(LogSession session, string outputPath, bool anonymize = true)
    {
        try
        {
            await AwaitCaptureStopAsync(session.Id).ConfigureAwait(false);
            if (!File.Exists(session.LogFilePath) || !PathHelper.IsSafeLocalPath(session.LogFilePath) ||
                !PathHelper.IsSafeLocalPath(outputPath) ||
                Path.GetFullPath(session.LogFilePath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)) return false;

            using var reader = new StreamReader(session.LogFilePath);
            await using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var jsonWriter = new System.Text.Json.Utf8JsonWriter(outStream, new System.Text.Json.JsonWriterOptions { Indented = true });

            jsonWriter.WriteStartArray();

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parsed = ParseLogLine(line);
                // Raw exports are never produced by the product. Keep the parameter for
                // source compatibility, but always redact sensitive device/game data.
                var message = SecurityHelper.RedactSensitiveText(parsed["Message"]);

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
            if (line.Length > 30 && line[2] == '-' && line[5] == ' ' && line[14] == '.')
            {
                result["Timestamp"] = line.Substring(0, 18);
                var match = Regex.Match(line, @"^\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+\d+\s+\d+\s+([VDIWEFA])\s+(.*)$");
                if (match.Success)
                {
                    result["Level"] = match.Groups[1].Value[0] switch
                    {
                        'F' or 'A' => "Fatal",
                        'E' => "Error",
                        'W' => "Warning",
                        'I' => "Info",
                        'D' => "Debug",
                        'V' => "Verbose",
                        _ => "Unknown"
                    };
                    result["Message"] = match.Groups[2].Value;
                }
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
            if (result["Level"] == "Unknown")
            {
                var iosLevel = Regex.Match(line, @"<(Fault|Error|Warning|Notice|Info|Default|Debug)>",
                    RegexOptions.IgnoreCase);
                if (iosLevel.Success)
                    result["Level"] = iosLevel.Groups[1].Value.ToUpperInvariant() switch
                    {
                        "FAULT" => "Fatal",
                        "ERROR" => "Error",
                        "WARNING" => "Warning",
                        "NOTICE" or "INFO" or "DEFAULT" => "Info",
                        "DEBUG" => "Debug",
                        _ => "Unknown"
                    };
            }
        }
        catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] ParseLogLine failed"); }

        return result;
    }

    private static string EscapeCsvCell(string value)
    {
        var trimmed = value.TrimStart();
        var safe = trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@'
            ? "'" + value
            : value;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }
}
