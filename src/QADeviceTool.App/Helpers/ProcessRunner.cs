using System.Diagnostics;

namespace QADeviceTool.Helpers;

/// <summary>
/// Safe wrapper for running external processes (adb, scrcpy, idevice_id, etc.)
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Runs a command and returns its stdout output.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(string fileName, string arguments, int timeoutMs = 10000)
    {
        var result = new ProcessResult();
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            var completed = await Task.Run(() => process.WaitForExit(timeoutMs));

            if (!completed)
            {
                process.Kill(true);
                result.Success = false;
                result.Error = "Process timed out.";
                return result;
            }

            result.Output = await outputTask;
            result.Error = await errorTask;
            result.ExitCode = process.ExitCode;
            result.Success = process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Starts a long-running process (e.g., logcat, syslog) and returns the Process handle.
    /// </summary>
    public static Process? StartLongRunning(string fileName, string arguments)
    {
        try
        {
            var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            return process;
        }
        catch
        {
            return null;
        }
    }
}

public class ProcessResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; } = -1;
}
