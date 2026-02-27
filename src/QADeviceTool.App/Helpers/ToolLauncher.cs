using System.Diagnostics;
using System.IO;

namespace QADeviceTool.Helpers;

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

    static ToolLauncher()
    {
        _toolsDir = Path.Combine(AppContext.BaseDirectory, "tools", "iMobileDevice");
    }

    public static string ToolsDirectory => _toolsDir;

    public static async Task<ToolLauncherResult> RunAsync(string exeName, string arguments, int timeoutMs = 15000, Action<string>? outputCallback = null)
    {
        var result = new ToolLauncherResult();
        var fullExePath = Path.Combine(_toolsDir, exeName);

        // Allow execution of scrcpy/adb via fallback or update them later.
        // Wait, the user specifically requested ALL native executions to go through this,
        // but ADB/Scrcpy are in a different folder. For iOS tools, they are strictly in iMobileDevice.
        // If exeName is an absolute path (from older code), just use it. Otherwise, assume iMobileDevice.
        if (Path.IsPathRooted(exeName))
        {
            fullExePath = exeName;
        }

        try
        {
            var logger = Services.AppLogger.Log;
            logger.Info($"[ToolLauncher] Launching: {fullExePath} {arguments}");
            logger.Debug($"[ToolLauncher] WorkingDirectory: {_toolsDir}");

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fullExePath,
                Arguments = arguments,
                WorkingDirectory = _toolsDir,
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

    public static Process? StartLongRunning(string exeName, string arguments)
    {
        var fullExePath = Path.Combine(_toolsDir, exeName);
        if (Path.IsPathRooted(exeName))
        {
            fullExePath = exeName;
        }

        try
        {
            var logger = Services.AppLogger.Log;
            logger.Info($"[ToolLauncher] StartLongRunning: {fullExePath} {arguments}");
            logger.Debug($"[ToolLauncher] WorkingDirectory: {_toolsDir}");

            var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fullExePath,
                Arguments = arguments,
                WorkingDirectory = _toolsDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            Services.ProcessManagerService.TrackProcess(process);
            logger.Info($"[ToolLauncher] Started LongRunning PID: {process.Id}");
            return process;
        }
        catch (Exception ex)
        {
            try { Services.AppLogger.Log.Error(ex, $"[ToolLauncher] Exception in StartLongRunning for {fullExePath}"); } catch {}
            return null;
        }
    }
}
