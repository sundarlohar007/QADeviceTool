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
    private static readonly System.Text.RegularExpressions.Regex _deviceKeyRegex =
        new(@"(?:-s|--udid)\s+(\S+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task<IDisposable> EnterDeviceGateAsync(string arguments)
    {
        var m = _deviceKeyRegex.Match(arguments);
        if (!m.Success)
        {
            await _globalOnly.WaitAsync();
            return new GateRelease(_globalOnly, null);
        }

        var deviceLock = _deviceLocks.GetOrAdd(m.Groups[1].Value, _ => new SemaphoreSlim(1, 1));
        await _globalCap.WaitAsync();
        await deviceLock.WaitAsync();
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
            return await _globalOnly.WaitAsync(waitMs).ConfigureAwait(false)
                ? new GateRelease(_globalOnly, null) : null;
        }

        var deviceLock = _deviceLocks.GetOrAdd(m.Groups[1].Value, _ => new SemaphoreSlim(1, 1));
        if (waitMs > 0)
        {
            var ok = await _globalCap.WaitAsync(waitMs).ConfigureAwait(false);
            var deviceOk = ok && await deviceLock.WaitAsync(waitMs).ConfigureAwait(false);
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

    public static async Task<ToolLauncherResult> RunAsync(string exeName, string arguments, int timeoutMs = 15000, Action<string>? outputCallback = null)
    {
        var result = new ToolLauncherResult();
        var fullExePath = ResolveExecutablePath(exeName);

        using var gate = await EnterDeviceGateAsync(arguments).ConfigureAwait(false);

        try
        {
            var logger = Services.AppLogger.Log;
            var workDir = ResolveWorkDir(fullExePath);
            var logArgs = PreferencesService.Current.SecureMode ? SanitizeForLog(arguments) : arguments;
            logger.Info($"[ToolLauncher] Launching: {fullExePath} {logArgs}");
            logger.Debug($"[ToolLauncher] WorkingDirectory: {workDir}");

            using var process = new Process();
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

            process.Start();
            Services.ProcessManagerService.TrackProcess(process);

            var fullOutput = new System.Text.StringBuilder();
            var fullError = new System.Text.StringBuilder();

            var outputTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    fullOutput.AppendLine(line);
                    outputCallback?.Invoke(line);
                }
            });

            var errorTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    fullError.AppendLine(line);
                }
            });

            var completed = await Task.Run(() => process.WaitForExit(timeoutMs));

            if (!completed)
            {
                try { process.CloseMainWindow(); } catch { }
                await Task.Delay(1000);
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                result.Success = false;
                result.Error = "Process timed out.";
                logger.Error($"[ToolLauncher] TIMEOUT: {fullExePath}");
                return result;
            }

            await Task.WhenAll(outputTask, errorTask);

            result.Output = fullOutput.ToString().Trim();
            result.Error = fullError.ToString().Trim();
            result.ExitCode = process.ExitCode;
            result.Success = process.ExitCode == 0;

            logger.Info($"[ToolLauncher] ExitCode: {result.ExitCode} | Success: {result.Success}");

            if (!string.IsNullOrWhiteSpace(result.Output))
                logger.Debug($"[ToolLauncher] STDOUT:\n{result.Output}");

            if (!string.IsNullOrWhiteSpace(result.Error))
                logger.Error($"[ToolLauncher] STDERR:\n{result.Error}");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            try { Services.AppLogger.Log.Error(ex, $"[ToolLauncher] Exception launching {fullExePath}"); } catch (Exception _) { AppLogger.Log.Debug(_, "[ToolLauncher] Exception during startup"); }
        }

        return result;
    }

    public static Process? StartLongRunning(string exeName, string arguments, Action<string>? errorCallback = null, bool drainStdout = true)
    {
        var fullExePath = ResolveExecutablePath(exeName);

        try
        {
            var logger = Services.AppLogger.Log;
            var workDir = ResolveWorkDir(fullExePath);
            var logArgs2 = PreferencesService.Current.SecureMode ? SanitizeForLog(arguments) : arguments;
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

            process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            process.Start();
            Services.ProcessManagerService.TrackProcess(process);

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
                        errorCallback?.Invoke(line);
                        logger.Warn($"[ToolLauncher] STDERR(long): {line}");
                    }
                }
                catch (Exception ex) { AppLogger.Log.Debug(ex, "[ToolLauncher] stderr stream ended"); }
            });

            logger.Info($"[ToolLauncher] Started LongRunning PID: {process.Id} (stderr draining)");
            return process;
        }
        catch (Exception ex)
        {
            try { Services.AppLogger.Log.Error(ex, $"[ToolLauncher] Exception in StartLongRunning for {fullExePath}"); } catch (Exception _) { AppLogger.Log.Debug(_, "[ToolLauncher] Exception during startup"); }
            return null;
        }
    }

    /// <summary>Sanitizes command arguments for logging when Secure Mode is enabled.</summary>
    private static string SanitizeForLog(string arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return arguments;
        // Redact device serials: -s {serial} -> -s [REDACTED]
        var sanitized = System.Text.RegularExpressions.Regex.Replace(arguments, @"-s\s+\S+", "-s [REDACTED]");
        // Redact file paths containing /sdcard/ or /data/
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"(/sdcard/|/data/)\S+", "${1}[PATH]");
        return sanitized;
    }
}
