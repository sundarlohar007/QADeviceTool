using System.Diagnostics;
using System.IO;

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

        try
        {
            var logger = Services.AppLogger.Log;
            var workDir = ResolveWorkDir(fullExePath);
            logger.Info($"[ToolLauncher] Launching: {fullExePath} {arguments}");
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
                while (!process.StandardOutput.EndOfStream)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line != null)
                    {
                        fullOutput.AppendLine(line);
                        outputCallback?.Invoke(line);
                    }
                }
            });

            var errorTask = Task.Run(async () =>
            {
                while (!process.StandardError.EndOfStream)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line != null)
                    {
                        fullError.AppendLine(line);
                    }
                }
            });

            var completed = await Task.Run(() => process.WaitForExit(timeoutMs));

            if (!completed)
            {
                process.Kill(true);
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
            try { Services.AppLogger.Log.Error(ex, $"[ToolLauncher] Exception launching {fullExePath}"); } catch {}
        }

        return result;
    }

    public static Process? StartLongRunning(string exeName, string arguments, Action<string>? errorCallback = null)
    {
        var fullExePath = ResolveExecutablePath(exeName);

        try
        {
            var logger = Services.AppLogger.Log;
            var workDir = ResolveWorkDir(fullExePath);
            logger.Info($"[ToolLauncher] StartLongRunning: {fullExePath} {arguments}");
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
                catch { /* stream ended */ }
            });

            logger.Info($"[ToolLauncher] Started LongRunning PID: {process.Id} (stderr draining)");
            return process;
        }
        catch (Exception ex)
        {
            try { Services.AppLogger.Log.Error(ex, $"[ToolLauncher] Exception in StartLongRunning for {fullExePath}"); } catch {}
            return null;
        }
    }
}
