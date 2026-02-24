using System.Diagnostics;

namespace QADeviceTool.Helpers;

/// <summary>
/// Safe wrapper for running external processes (adb, scrcpy, idevice_id, etc.)
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Runs a command and returns its stdout output. Optionally streams lines back as they arrive.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(string fileName, string arguments, int timeoutMs = 10000, Action<string>? outputCallback = null)
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

            var fullOutput = new System.Text.StringBuilder();
            var fullError = new System.Text.StringBuilder();

            // Background task to read stdout and trigger callback
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

            // Background task for stderr
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
                return result;
            }

            await Task.WhenAll(outputTask, errorTask);

            result.Output = fullOutput.ToString();
            result.Error = fullError.ToString();
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
