using System.Diagnostics;
using System.IO;
using LogPro.Services;

namespace LogPro.Helpers;

public class ToolLauncherResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; } = -1;
}

public static class ToolLauncher
{
    private static readonly string _toolsDir;
    private static readonly string _pymobileDeviceDir;

    // §9.1 concurrency policy: per-device lock + global cap. Serialized per device,
    // parallel across devices, bounded subprocess count. Long-running processes
    // (StartLongRunning) intentionally bypass the gate — they'd hold it for hours.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim _globalCap = new(Environment.ProcessorCount);
    private static readonly SemaphoreSlim _globalOnly = new(Environment.ProcessorCount);
    private static readonly SemaphoreSlim _longRunningCap = new(Math.Max(1, Environment.ProcessorCount));
    private static readonly System.Text.RegularExpressions.Regex _deviceKeyRegex =
        new(@"(?:-s|--udid)\s+(\S+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _deviceArgumentRegex = new(
        @"(?:^|\s)(?:-s|--udid)\s+(?:""(?<selector>[^""]+)""|(?<selector>\S+))",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private const int MaxCapturedOutputChars = 1_000_000;

    private static async Task<IDisposable> EnterDeviceGateAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var m = _deviceKeyRegex.Match(arguments);
        if (!m.Success)
        {
            await _globalOnly.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GateRelease(_globalOnly, null);
        }

        var deviceLock = _deviceLocks.GetOrAdd(m.Groups[1].Value, _ => new SemaphoreSlim(1, 1));
        await _globalCap.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await deviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _globalCap.Release();
            throw;
        }
        return new GateRelease(_globalCap, deviceLock);
    }

    private sealed class GateRelease : IDisposable
    {
        private readonly SemaphoreSlim _global;
        private readonly SemaphoreSlim? _device;
        private int _disposed;
        public GateRelease(SemaphoreSlim global, SemaphoreSlim? device) { _global = global; _device = device; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _device?.Release();
            _global.Release();
        }
    }

    /// <summary>Test hook: acquire the gate without launching a process. null = timed out.</summary>
    internal static async Task<IDisposable?> TestAcquireAsync(string arguments, int waitMs = 0)
    {
        var m = _deviceKeyRegex.Match(arguments);
        if (!m.Success)
        {
            if (waitMs > 0)
                return await _globalOnly.WaitAsync(waitMs).ConfigureAwait(false)
                    ? new GateRelease(_globalOnly, null) : null;
            await _globalOnly.WaitAsync().ConfigureAwait(false);
            return new GateRelease(_globalOnly, null);
        }

        var deviceLock = _deviceLocks.GetOrAdd(m.Groups[1].Value, _ => new SemaphoreSlim(1, 1));
        if (waitMs > 0)
        {
            var ok = await _globalCap.WaitAsync(waitMs).ConfigureAwait(false);
            var deviceOk = ok && await deviceLock.WaitAsync(waitMs).ConfigureAwait(false);
            if (ok && !deviceOk) _globalCap.Release(); // don't leak the global slot
            return (ok && deviceOk) ? new GateRelease(_globalCap, deviceLock) : null;
        }

        await _globalCap.WaitAsync().ConfigureAwait(false);
        await deviceLock.WaitAsync().ConfigureAwait(false);
        return new GateRelease(_globalCap, deviceLock);
    }

    static ToolLauncher()
    {
        _toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        _pymobileDeviceDir = Path.Combine(_toolsDir, "pymobiledevice3");
    }

    public static string ToolsDirectory => _toolsDir;

    /// <summary>
    /// Picks a stable working directory. For rooted tool paths inside our bundled tools/ tree
    /// (e.g. tools/pymobiledevice3/.../pymobiledevice3.exe) we use the exe's own directory.
    /// For external tools (system python, system adb), we anchor to ToolsDirectory if it exists,
    /// else AppContext.BaseDirectory — never the exe's install dir, which avoids leaking pymd3
    /// pairing files into the user's Python install.
    /// </summary>
    private static string ResolveWorkDir(string fullExePath)
    {
        var appBase = AppContext.BaseDirectory;
        var exeDir = Path.GetDirectoryName(fullExePath);
        if (!string.IsNullOrEmpty(exeDir) &&
            exeDir.StartsWith(appBase, System.StringComparison.OrdinalIgnoreCase))
        {
            return exeDir;
        }
        if (Directory.Exists(_pymobileDeviceDir))
            return _pymobileDeviceDir;
        return Directory.Exists(_toolsDir) ? _toolsDir : appBase;
    }

    private static string ResolveExecutablePath(string exeName)
    {
        if (Path.IsPathRooted(exeName))
            return exeName;

        var bundledPath = Path.Combine(_toolsDir, exeName);
        return File.Exists(bundledPath) ? bundledPath : exeName;
    }

    public static async Task<ToolLauncherResult> RunAsync(string exeName, string arguments, int timeoutMs = 15000,
        Action<string>? outputCallback = null, CancellationToken cancellationToken = default)
    {
        var result = new ToolLauncherResult();
        var fullExePath = ResolveExecutablePath(exeName);

        var selector = _deviceArgumentRegex.Match(arguments);
        if (selector.Success && !SecurityHelper.IsValidOfflineDeviceSelector(selector.Groups["selector"].Value))
        {
            result.Error = "Blocked by LogPro offline security policy: network device selectors are disabled.";
            return result;
        }

        if (SecurityHelper.IsNetworkCapableCommand(arguments))
        {
            result.Error = "Blocked by LogPro offline security policy.";
            return result;
        }

        using var gate = await EnterDeviceGateAsync(arguments, cancellationToken).ConfigureAwait(false);
        Process? process = null;
        Task outputTask = Task.CompletedTask;
        Task errorTask = Task.CompletedTask;

        try
        {
            var logger = Services.AppLogger.Log;
            var workDir = ResolveWorkDir(fullExePath);
            var logArgs = SanitizeForLog(arguments);
            logger.Info($"[ToolLauncher] Launching: {fullExePath} {logArgs}");
            logger.Debug($"[ToolLauncher] WorkingDirectory: {workDir}");

            process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fullExePath,
                Arguments = arguments,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ConfigureOfflineEnvironment(process.StartInfo);

            process.Start();
            Services.ProcessManager.Instance.TrackProcess(process);

            var fullOutput = new System.Text.StringBuilder();
            var fullError = new System.Text.StringBuilder();

            outputTask = DrainOutputAsync(process.StandardOutput, fullOutput, outputCallback);
            errorTask = DrainOutputAsync(process.StandardError, fullError, null);

            using var timeoutCts = new CancellationTokenSource(Math.Max(1, timeoutMs));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var cancelled = false;
            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || timeoutCts.IsCancellationRequested)
            {
                cancelled = true;
            }

            if (cancelled)
            {
                await TerminateProcessAsync(process).ConfigureAwait(false);
                result.Success = false;
                result.Error = cancellationToken.IsCancellationRequested
                    ? "Process cancelled."
                    : "Process timed out.";
                logger.Error($"[ToolLauncher] {(cancellationToken.IsCancellationRequested ? "CANCELLED" : "TIMEOUT")}: {fullExePath}");
            }

            await AwaitReaderAsync(outputTask).ConfigureAwait(false);
            await AwaitReaderAsync(errorTask).ConfigureAwait(false);

            if (cancelled) return result;

            result.Output = fullOutput.ToString().Trim();
            result.Error = fullError.ToString().Trim();
            result.ExitCode = process.ExitCode;
            result.Success = process.ExitCode == 0;

            logger.Info($"[ToolLauncher] ExitCode: {result.ExitCode} | Success: {result.Success}");

            if (!string.IsNullOrWhiteSpace(result.Output))
                logger.Debug($"[ToolLauncher] STDOUT:\n{SecurityHelper.RedactSensitiveText(result.Output)}");

            if (!string.IsNullOrWhiteSpace(result.Error))
                logger.Error($"[ToolLauncher] STDERR:\n{SecurityHelper.RedactSensitiveText(result.Error)}");
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "Process cancelled.";
            if (process != null) await TerminateProcessAsync(process).ConfigureAwait(false);
            await AwaitReaderAsync(outputTask).ConfigureAwait(false);
            await AwaitReaderAsync(errorTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = SecurityHelper.RedactSensitiveText(ex.Message);
            try { Services.AppLogger.Log.Error(ex, $"[ToolLauncher] Exception launching {fullExePath}"); } catch (Exception _) { AppLogger.Log.Debug(_, "[ToolLauncher] Exception during startup"); }
        }
        finally
        {
            process?.Dispose();
        }

        return result;
    }

    public static Process? StartLongRunning(string exeName, string arguments, Action<string>? errorCallback = null, bool drainStdout = true)
    {
        var fullExePath = ResolveExecutablePath(exeName);

        var selector = _deviceArgumentRegex.Match(arguments);
        if (selector.Success && !SecurityHelper.IsValidOfflineDeviceSelector(selector.Groups["selector"].Value))
        {
            AppLogger.Log.Warn("[ToolLauncher] Blocked network device selector in long-running command");
            return null;
        }

        if (SecurityHelper.IsNetworkCapableCommand(arguments))
        {
            AppLogger.Log.Warn($"[ToolLauncher] Blocked network-capable long-running command: {SecurityHelper.RedactSensitiveText(arguments)}");
            return null;
        }

        if (!_longRunningCap.Wait(0))
        {
            AppLogger.Log.Warn("[ToolLauncher] Long-running process cap reached; launch rejected");
            return null;
        }
        var longRunningSlotReleased = 0;

        try
        {
            var logger = Services.AppLogger.Log;
            var workDir = ResolveWorkDir(fullExePath);
            var logArgs2 = SanitizeForLog(arguments);
            logger.Info($"[ToolLauncher] StartLongRunning: {fullExePath} {logArgs2}");
            logger.Debug($"[ToolLauncher] WorkingDirectory: {workDir}");

            var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fullExePath,
                Arguments = arguments,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            ConfigureOfflineEnvironment(process.StartInfo);
            process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (Interlocked.Exchange(ref longRunningSlotReleased, 1) == 0)
                    _longRunningCap.Release();
            };
            process.Start();
            Services.ProcessManager.Instance.TrackProcess(process);

            // Drain stdout in background to prevent pipe buffer deadlock (4KB on Windows).
            // Callers that attach own OutputDataReceived handler (SessionService) pass drainStdout: false.
            if (drainStdout)
            {
                process.BeginOutputReadLine();
            }

            // Drain stderr in background to prevent buffer deadlock.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync() is { } line)
                    {
                        var safeLine = SecurityHelper.RedactSensitiveText(line);
                        errorCallback?.Invoke(safeLine);
                        logger.Warn($"[ToolLauncher] STDERR(long): {safeLine}");
                    }
                }
                catch (Exception ex) { AppLogger.Log.Debug(ex, "[ToolLauncher] stderr stream ended"); }
            });

            logger.Info($"[ToolLauncher] Started LongRunning PID: {process.Id} (stderr draining)");
            return process;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref longRunningSlotReleased, 1) == 0)
                _longRunningCap.Release();
            try { Services.AppLogger.Log.Error(ex, $"[ToolLauncher] Exception in StartLongRunning for {fullExePath}"); } catch (Exception _) { AppLogger.Log.Debug(_, "[ToolLauncher] Exception during startup"); }
            return null;
        }
    }

    /// <summary>Sanitizes command arguments for logging when Secure Mode is enabled.</summary>
    private static string SanitizeForLog(string arguments)
        => SecurityHelper.RedactSensitiveText(arguments);

    internal static void ConfigureOfflineEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var name in new[]
        {
            "ADB_SERVER_SOCKET", "ADB_MDNS_AUTO_CONNECT", "ADB_MDNS_OPENSCREEN",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
            "http_proxy", "https_proxy", "all_proxy", "no_proxy",
            "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP"
        })
        {
            startInfo.EnvironmentVariables.Remove(name);
        }

        // Prevent ADB from opting into mDNS/network discovery through inherited defaults.
        startInfo.EnvironmentVariables["ADB_MDNS_AUTO_CONNECT"] = "0";
        startInfo.EnvironmentVariables["ADB_MDNS_OPENSCREEN"] = "0";
        startInfo.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
    }

    private static async Task DrainOutputAsync(StreamReader reader, System.Text.StringBuilder buffer, Action<string>? callback)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (buffer.Length < MaxCapturedOutputChars)
                {
                    var remaining = MaxCapturedOutputChars - buffer.Length;
                    buffer.AppendLine(line.Length <= remaining ? line : line[..remaining]);
                }
                try { callback?.Invoke(line); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[ToolLauncher] output callback failed"); }
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { process.Kill(entireProcessTree: false); }
            }
        }
        catch { }

        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private static async Task AwaitReaderAsync(Task readerTask)
    {
        try
        {
            await readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { }
    }
}
