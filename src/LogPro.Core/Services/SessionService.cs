using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

public sealed record NumberedLogLine(long Sequence, string Text);
public sealed record CaptureBatch(string SessionId, IReadOnlyList<NumberedLogLine> Lines);
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
    private readonly object _lifecycleLock = new();
    private readonly HashSet<Task<bool>> _startTasks = new();
    private readonly object _bufferLock = new();
    private string _sessionsRootDirectory;
    private int _shutdownGeneration;
    private volatile bool _shutdown;
    private const int DisplayQueueLimit = 10000;

    /// <summary>
    /// Fired with batched log lines (every 200ms) instead of per-line.
    /// The string key is the session Id so consumers can filter to their session.
    /// </summary>
    public event Action<string, string>? LogBatchReceived;
    public event Action<CaptureBatch>? CaptureBatchReceived;
    public IReadOnlyList<NumberedLogLine>? GetLiveSnapshot(string sessionId)
    {
        if (!_activeCaptures.TryGetValue(sessionId, out var ctx)) return null;
        lock (ctx.WriterLock) return ctx.RecentLines.ToArray();
    }
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
        => CreateSessionAt(device, customSessionName, SessionsRootDirectory);

    public LogSession CreateSessionAt(DeviceInfo device, string? customSessionName, string rootDirectory)
    {
        if (!PathHelper.TryGetSafeLocalDirectory(rootDirectory, out var safeRoot))
            throw new ArgumentException("Session output must be a safe local directory.", nameof(rootDirectory));
        var deviceHash = SecurityHelper.HashSerial(device.Serial);
        var sessionName = SecurityHelper.GetSafeSessionName(customSessionName, deviceHash, device.Platform.ToString());

        var sessionDir = PathHelper.CreateSessionDirectory(sessionName, safeRoot);
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
    public Task<bool> StartCaptureAsync(LogSession session, LogcatBuffer buffer = LogcatBuffer.Main, LogcatFormat format = LogcatFormat.ThreadTime)
        => StartCaptureAsync(session, new CaptureOptions(buffer, format, session.TargetPackage ?? PreferencesService.Current.TargetPackageName));

    public Task<bool> StartCaptureAsync(LogSession session, CaptureOptions options, CancellationToken token = default)
    {
        lock (_lifecycleLock)
        {
            if (_shutdown || token.IsCancellationRequested) return Task.FromResult(false);
            var task = StartCaptureCoreAsync(session, options, token);
            _startTasks.Add(task);
            _ = task.ContinueWith(completed => { lock (_lifecycleLock) _startTasks.Remove(completed); }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<bool> StartCaptureCoreAsync(LogSession session, CaptureOptions options, CancellationToken token)
    {
        var buffer = options.Buffer;
        var format = options.Format;
        if (_shutdown) return false;
        var generation = Volatile.Read(ref _shutdownGeneration);
        var stopRevision = Interlocked.Read(ref session.StopRevision);
        await AwaitCaptureStopAsync(session.Id).ConfigureAwait(false);
        if (_shutdown || token.IsCancellationRequested || stopRevision != Interlocked.Read(ref session.StopRevision) || generation != Volatile.Read(ref _shutdownGeneration)) return false;
        if (_activeCaptures.Values.Any(ctx => ctx.Session.DeviceSerial.Equals(session.DeviceSerial, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!_startingDevices.TryAdd(session.DeviceSerial, 0)) return false;
        session.CaptureError = string.Empty;
        session.CaptureNotice = string.Empty;
        session.ExitCode = null;

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
            session.CaptureError = SecurityHelper.RedactSensitiveText(ex.Message);
            AppLogger.Log.Error(ex, "[SessionService] Failed to start capture process");
            return false;
        }

        if (process == null)
        {
            session.CaptureError = "The logging tool could not start. Check dependency health, USB authorization and device trust.";
            _startingDevices.TryRemove(session.DeviceSerial, out _);
            return false;
        }
        await Task.Delay(250).ConfigureAwait(false);
        if (process.HasExited || stopRevision != Interlocked.Read(ref session.StopRevision) || token.IsCancellationRequested || generation != Volatile.Read(ref _shutdownGeneration))
        {
            session.CaptureError = ToolLauncher.GetProcessError(process);
            if (string.IsNullOrWhiteSpace(session.CaptureError)) session.CaptureError = "Capture stopped during startup. Reconnect and authorize the device, then retry.";
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { process.Dispose(); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[SessionService] Process dispose error"); }
            _startingDevices.TryRemove(session.DeviceSerial, out _);
            return false;
        }

        string targetPackageName = options.TargetPackage;

        StreamWriter? writer = null;
        StreamWriter? appWriter = null;
        try
        {
            writer = new StreamWriter(session.LogFilePath, append: true);
            // FEAT-21: mark restarts so appended captures are distinguishable
            if (new FileInfo(session.LogFilePath) is { Length: > 0 })
            {
                writer.WriteLine("--- SESSION RESTARTED ---");
                session.LogLineCount++;
            }
            if (session.Platform == DevicePlatform.Android && !string.IsNullOrWhiteSpace(targetPackageName) &&
                format is (LogcatFormat.Raw or LogcatFormat.Tag or LogcatFormat.Long))
                session.CaptureNotice = "Full logs are captured. App-only filtering requires ThreadTime, Time, Brief, Thread or Process format.";
            if (session.Platform == DevicePlatform.Android && !string.IsNullOrWhiteSpace(targetPackageName) &&
                string.IsNullOrEmpty(session.CaptureNotice))
            {
                session.AppLogFilePath = Path.Combine(session.SessionDirectory, $"{session.Platform}_{session.DeviceId}_app_log.txt");
                appWriter = new StreamWriter(session.AppLogFilePath, append: true);
            }
        }
        catch (Exception ex)
        {
            session.CaptureError = $"Cannot write session logs: {SecurityHelper.RedactSensitiveText(ex.Message)}";
            AppLogger.Log.Error(ex, "Failed to create log writers");
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            writer?.Dispose();
            appWriter?.Dispose();
            _startingDevices.TryRemove(session.DeviceSerial, out _);
            return false;
        }

        var cts = new CancellationTokenSource();
        var ctx = new CaptureContext(process, writer, appWriter, session, cts, new ConcurrentQueue<NumberedLogLine>());

        lock (_lifecycleLock)
        {
            // TryAdd: if a capture for this session already exists, clean up and return false
            if (_shutdown || stopRevision != Interlocked.Read(ref session.StopRevision) || token.IsCancellationRequested || !_activeCaptures.TryAdd(session.Id, ctx))
            {
                process.Kill(session.Platform == DevicePlatform.iOS);
                process.Dispose();
                writer.Dispose();
                appWriter?.Dispose();
                cts.Dispose();
                _startingDevices.TryRemove(session.DeviceSerial, out _);
                return false;
            }

            session.EndTime = null;
            session.Status = SessionStatus.Capturing;
            if (session.LogLineCount == 0) session.StartTime = DateTime.Now;
            if (session.StopReason is "stream-exit" or "connection-lost")
            {
                session.LastResumedUtc = DateTime.UtcNow;
                AppendCaptureEvent(session, "resumed");
            }
            session.StopReason = "";
            session.CaptureComplete = false;
            session.Format = format;
            session.Buffer = buffer;
            session.TargetPackage = options.TargetPackage;
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
                        catch (Exception ex) { FailCapture(ctx, ex); }
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
                                if (currentTargetPid != (pid ?? string.Empty))
                                {
                                    Volatile.Write(ref currentTargetPid, pid ?? string.Empty);
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
                        if (ctx.WritersClosed) { session.CaptureComplete = false; return; }
                        {
                            writer.WriteLine(line);
                            if (appWriter != null && !string.IsNullOrWhiteSpace(currentTargetPid) &&
                                MatchesLogcatPid(line, Volatile.Read(ref currentTargetPid), format))
                                appWriter.WriteLine(line);
                        }
                    }

                    NumberedLogLine numbered;
                    lock (ctx.WriterLock)
                    {
                        session.LogLineCount++;
                        session.LastLogUtc = DateTime.UtcNow;
                        numbered = new NumberedLogLine(session.LogLineCount, line);
                        ctx.RecentLines.Enqueue(numbered);
                        while (ctx.RecentLines.Count > 20000) ctx.RecentLines.Dequeue();
                    }
                    var crash = session.Crashes.ScanLine(line, (int)Math.Min(int.MaxValue, numbered.Sequence - 1), session.Platform);
                    if (crash != null)
                    {
                        var safeCrash = new CrashDetector.CrashEvent
                        {
                            Timestamp = crash.Timestamp,
                            Pattern = crash.Pattern,
                            Line = SecurityHelper.RedactSensitiveText(crash.Line),
                            Platform = crash.Platform,
                            LineIndex = crash.LineIndex
                        };
                        File.AppendAllText(Path.Combine(session.SessionDirectory, "crashes.jsonl"), JsonSerializer.Serialize(safeCrash) + Environment.NewLine);
                    }
                    ctx.Buffer.Enqueue(numbered);
                    while (ctx.Buffer.Count > DisplayQueueLimit && ctx.Buffer.TryDequeue(out var discardedLine))
                        Interlocked.Increment(ref ctx.DroppedLines);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref ctx.DroppedLines);
                    FailCapture(ctx, ex);
                }
            };
            PublishCaptureEvent(CaptureStarted, session);
            // Attach the exit handler before enabling events so a fast tool/device disconnect
            // cannot leave a capture permanently marked as active.
            process.Exited += (_, _) => _ = Task.Run(() => HandleCaptureExit(session, process));
            try { process.BeginOutputReadLine(); }
            catch (Exception ex) { FailCapture(ctx, ex); return false; }
            process.EnableRaisingEvents = true;
            if (generation != Volatile.Read(ref _shutdownGeneration)) { StopCapture(session); return false; }
            try { if (process.HasExited) { StopCapture(session); return false; } }
            catch (InvalidOperationException) { return false; }

            AppLogger.Log.Info($"Capture started for device {session.DeviceId}");
            return true;
        }
    }

    internal void HandleCaptureExit(LogSession session, Process process)
    {
        lock (_lifecycleLock)
        {
            if (!_activeCaptures.TryGetValue(session.Id, out var owner) || !ReferenceEquals(owner.Process, process)) return;
            var error = ToolLauncher.GetProcessError(process);
            session.CaptureError = string.IsNullOrWhiteSpace(error)
                ? "Device log stream ended unexpectedly. Check the USB connection and reconnect." : error;
            session.StopReason = "stream-exit";
            try { session.ExitCode = process.ExitCode; } catch (InvalidOperationException) { }
            StopCapture(session);
        }
    }

    private void FailCapture(CaptureContext context, Exception exception)
    {
        lock (_lifecycleLock)
        {
            var session = context.Session;
            if (!_activeCaptures.TryGetValue(session.Id, out var owner) || !ReferenceEquals(owner, context)) return;
            session.CaptureError = "Capture output could not be saved: " + SecurityHelper.RedactSensitiveText(exception.Message);
            session.StopReason = "write-failure";
            session.CaptureComplete = false;
            AppLogger.Log.Error(exception, "Capture output failed");
            StopCapture(session);
        }
    }

    internal static bool MatchesLogcatPid(string line, string pid, LogcatFormat format)
    {
        if (string.IsNullOrWhiteSpace(pid)) return false;
        var pattern = format switch
        {
            LogcatFormat.ThreadTime => @"^\s*\d{2}-\d{2}\s+\S+\s+(?<pid>\d+)\s+\d+\s",
            LogcatFormat.Brief => @"^[VDIWEFAS]/.*?\(\s*(?<pid>\d+)\):",
            LogcatFormat.Time => @"^\d{2}-\d{2}\s+\S+\s+[VDIWEFAS]/.*?\(\s*(?<pid>\d+)\):",
            LogcatFormat.Process => @"^[VDIWEFAS]\(\s*(?<pid>\d+)\)",
            LogcatFormat.Thread => @"^[VDIWEFAS]\(\s*(?<pid>\d+):\s*\d+\)",
            _ => null
        };
        if (pattern == null) return false; // Raw/tag formats do not carry a reliable process identity.
        var match = Regex.Match(line, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success && pid.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(match.Groups["pid"].Value);
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
        lock (ctx.DisplayLock)
        {
            if (ctx.Buffer.IsEmpty) return;

            // Bound each timer pass and preserve order against the final stop flush.
            for (var batches = 0; batches < 5 && !ctx.Buffer.IsEmpty; batches++)
            {
                var numbered = new List<NumberedLogLine>();
                while (numbered.Count < 500 && ctx.Buffer.TryDequeue(out var line)) numbered.Add(line);
                var batch = new System.Text.StringBuilder();
                foreach (var line in numbered) batch.AppendLine(line.Text);
                if (numbered.Count > 0 && CaptureBatchReceived is { } observers)
                    foreach (Action<CaptureBatch> observer in observers.GetInvocationList())
                        try { observer(new CaptureBatch(sessionId, numbered)); }
                        catch (Exception ex) { AppLogger.Log.Warn(ex, "Capture observer failed"); }

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
    }

    internal static System.Text.StringBuilder DrainDisplayBatch(ConcurrentQueue<string> queue, int limit)
    {
        var batch = new System.Text.StringBuilder();
        for (var count = 0; count < limit && queue.TryDequeue(out var line); count++) batch.AppendLine(line);
        return batch;
    }

    public void StopCapture(LogSession session)
    {
        lock (_lifecycleLock)
        {
            Interlocked.Increment(ref session.StopRevision);
            if (!_activeCaptures.TryRemove(session.Id, out var ctx)) return;
            if (string.IsNullOrEmpty(session.StopReason)) session.StopReason = _shutdown ? "shutdown" : "requested";
            if (session.StopReason is "stream-exit" or "connection-lost")
            {
                session.InterruptionCount++;
                session.LastInterruptedUtc = DateTime.UtcNow;
                AppendCaptureEvent(session, session.StopReason);
            }

            session.Status = SessionStatus.Stopped;
            session.EndTime = DateTime.Now;
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

                    var drained = await Task.WhenAny(ctx.OutputCompleted.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) == ctx.OutputCompleted.Task;
                    if (!drained) session.CaptureError = "Capture incomplete: output did not drain before the shutdown deadline.";
                    session.CaptureComplete = drained && string.IsNullOrEmpty(session.CaptureError);
                    if (ctx.FlushTask != null) try { await Task.WhenAny(ctx.FlushTask, Task.Delay(2000)).ConfigureAwait(false); } catch { }
                    if (ctx.PidTask != null) try { await Task.WhenAny(ctx.PidTask, Task.Delay(2000)).ConfigureAwait(false); } catch { }
                    lock (ctx.WriterLock)
                    {
                        ctx.WritersClosed = true;
                        try { ctx.Writer.Flush(); ctx.AppWriter?.Flush(); }
                        catch (Exception ex) { session.CaptureComplete = false; session.CaptureError = "Final log flush failed: " + SecurityHelper.RedactSensitiveText(ex.Message); }
                        ctx.Writer.Dispose();
                        ctx.AppWriter?.Dispose();
                    }
                    while (!ctx.Buffer.IsEmpty) FlushCaptureBuffer(session.Id, ctx);
                }
                catch (Exception ex) { session.CaptureComplete = false; session.CaptureError = "Capture finalization failed: " + SecurityHelper.RedactSensitiveText(ex.Message); AppLogger.Log.Warn(ex, "[SessionService] StopCapture cleanup error"); }
                finally
                {
                    _startingDevices.TryRemove(session.DeviceSerial, out _);
                    ctx.Process.Dispose();
                    ctx.Cts.Dispose();
                    SaveSessionMetadata(session);
                    PublishCaptureEvent(CaptureStopped, session);
                    AppLogger.Log.Info($"Capture finalized: {session.DeviceId}; reason={session.StopReason}; exit={session.ExitCode}; saved={session.LogLineCount}; complete={session.CaptureComplete}; error={session.CaptureError}");
                }
            });
            lock (_stopTasks)
            {
                _stopTasks.RemoveAll(t => t.IsCompleted);
                _stopTasks.Add(stopTask);
            }
            _captureStopTasks[session.Id] = stopTask;

            SaveSessionMetadata(session);

            lock (_flushTimerLock)
            {
                if (_activeCaptures.IsEmpty)
                {
                    _flushTimer?.Dispose();
                    _flushTimer = null;
                }
            }

            AppLogger.Log.Info($"Capture stopped for device {session.DeviceId}. Duration: {session.EndTime - session.StartTime}");
        }
    }

    private static void AppendCaptureEvent(LogSession session, string reason)
    {
        try
        {
            File.AppendAllText(Path.Combine(session.SessionDirectory, "capture-events.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new { utc = DateTime.UtcNow, reason, savedLines = session.LogLineCount }) + Environment.NewLine);
        }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "Could not persist capture interruption event"); }
    }

    public void StopAllCaptures()
    {
        Task<bool>[] starts;
        lock (_lifecycleLock)
        {
            _shutdown = true;
            Interlocked.Increment(ref _shutdownGeneration);
            starts = _startTasks.ToArray();
        }
        try { if (!Task.WhenAll(starts).Wait(TimeSpan.FromSeconds(20))) AppLogger.Log.Warn("Capture startup cancellation timed out during shutdown"); }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "Capture startup failed during shutdown"); }
        foreach (var session in _activeCaptures.Values.Select(c => c.Session).ToList())
            StopCapture(session);

        // In-flight startup owns its reservation until it observes shutdown.

        lock (_flushTimerLock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        Task[] pendingStops;
        lock (_stopTasks) pendingStops = _stopTasks.ToArray();
        try
        {
            if (!Task.WhenAll(pendingStops).Wait(TimeSpan.FromSeconds(20)))
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
        Task? task;
        lock (_lifecycleLock) _captureStopTasks.TryGetValue(sessionId, out task);
        if (task != null)
        {
            await task.ConfigureAwait(false);
            _captureStopTasks.TryRemove(sessionId, out _);
        }
    }

    public Task WaitForCaptureStopAsync(LogSession session) => AwaitCaptureStopAsync(session.Id);

    private const string MetadataFileName = "session.json";
    private sealed record SessionMetadata(string Id, string Name, string DeviceId, string DeviceName,
        DevicePlatform Platform, string LogFileName, string AppLogFileName, DateTime StartTime, DateTime? EndTime,
        string CaptureError = "", string StopReason = "", int? ExitCode = null, bool CaptureComplete = false,
        long LogLineCount = 0, LogcatFormat Format = LogcatFormat.ThreadTime, LogcatBuffer Buffer = LogcatBuffer.Main,
        int InterruptionCount = 0, DateTime? LastInterruptedUtc = null, DateTime? LastResumedUtc = null);

    private static void SaveSessionMetadata(LogSession session)
    {
        string? pendingPath = null;
        try
        {
            if (!PathHelper.IsSafeLocalPath(session.SessionDirectory)) return;
            var metadata = new SessionMetadata(session.Id, session.Name, session.DeviceId,
                SecurityHelper.RedactSensitiveText(session.DeviceName), session.Platform,
                Path.GetFileName(session.LogFilePath), Path.GetFileName(session.AppLogFilePath), session.StartTime, session.EndTime, session.CaptureError, session.StopReason, session.ExitCode, session.CaptureComplete, session.LogLineCount, session.Format, session.Buffer, session.InterruptionCount, session.LastInterruptedUtc, session.LastResumedUtc);
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
                            session.CaptureError = metadata.CaptureError;
                            session.StopReason = metadata.StopReason;
                            session.ExitCode = metadata.ExitCode;
                            session.CaptureComplete = metadata.CaptureComplete;
                            session.LogLineCount = metadata.LogLineCount;
                            session.Format = metadata.Format;
                            session.Buffer = metadata.Buffer;
                            session.InterruptionCount = metadata.InterruptionCount;
                            session.LastInterruptedUtc = metadata.LastInterruptedUtc;
                            session.LastResumedUtc = metadata.LastResumedUtc;
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
                var crashesPath = Path.Combine(dir, "crashes.jsonl");
                if (File.Exists(crashesPath) && PathHelper.IsSafeLocalPath(crashesPath))
                {
                    foreach (var record in File.ReadLines(crashesPath))
                    {
                        try { if (JsonSerializer.Deserialize<CrashDetector.CrashEvent>(record) is { } crash) session.Crashes.Restore(crash); }
                        catch (JsonException) { /* A terminated write may leave one partial record. */ }
                    }
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
        public object DisplayLock { get; } = new();
        public CaptureContext(Process process, StreamWriter writer, StreamWriter? appWriter, LogSession session,
            CancellationTokenSource cts, ConcurrentQueue<NumberedLogLine> buffer)
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
        public ConcurrentQueue<NumberedLogLine> Buffer { get; }
        public Queue<NumberedLogLine> RecentLines { get; } = new();
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

            var parser = new LogStreamParser(session.Format);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var record = parser.Parse(line);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parsed = new Dictionary<string, string> { ["Timestamp"] = record.Timestamp, ["Level"] = record.Level.ToString(), ["Message"] = record.Message };
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

            var parser = new LogStreamParser(session.Format);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var record = parser.Parse(line);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parsed = new Dictionary<string, string> { ["Timestamp"] = record.Timestamp, ["Level"] = record.Level.ToString(), ["Message"] = record.Message };
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
        var parsed = LogLineParser.Parse(line);
        return new() { ["Timestamp"] = parsed.Timestamp, ["Level"] = parsed.Level.ToString(), ["Message"] = parsed.Message };
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
