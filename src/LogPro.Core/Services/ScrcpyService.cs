using System.Text.RegularExpressions;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Controls scrcpy for Android screen mirroring.
/// Uses ToolResolver to find bundled or system scrcpy.
/// </summary>
public class ScrcpyService : IScrcpyService
{
    public event Action? StateChanged;
    private string _scrcpy => ToolResolver.Resolve("scrcpy");
    private readonly object _lifecycleLock = new();
    private System.Diagnostics.Process? _mirrorProcess;
    private long _mirrorGeneration;

    public ScrcpyService()
    {

    }

    public async Task<ToolStatus> CheckAvailabilityAsync()
    {
        var status = new ToolStatus
        {
            Name = "scrcpy (Screen Mirror)",
            Description = "Required for Android screen mirroring"
        };

        var result = await ToolLauncher.RunAsync(_scrcpy, "--version").ConfigureAwait(false);
        if (result.Success)
        {
            status.IsInstalled = true;
            var match = Regex.Match(result.Output, @"(\d+\.\d+(\.\d+)?)");
            status.Version = match.Success ? match.Groups[1].Value : "Installed";
            status.Path = ToolResolver.IsBundled(_scrcpy) ? $"Bundled: {_scrcpy}" : (PathHelper.FindInPath("scrcpy") ?? "In PATH");
            status.StatusMessage = "scrcpy is ready for screen mirroring";
        }
        else
        {
            AppLogger.Log.Warn($"[ScrcpyService] CheckAvailabilityAsync failed. Error: {SecurityHelper.RedactSensitiveText(result.Error)}, Output: {SecurityHelper.RedactSensitiveText(result.Output)}");
            status.IsInstalled = false;
            status.StatusMessage = $"scrcpy could not run: {SecurityHelper.RedactSensitiveText(result.Error)}";
        }

        return status;
    }

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                try { return _mirrorProcess != null && !_mirrorProcess.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    public string? MirroredDeviceSerial { get; private set; }

    public string? LastError { get; private set; }

    public async Task<bool> StartMirroringAsync(string serial, ScrcpyOptions? options = null)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial))
        {
            LastError = "Network device selectors are disabled by offline security policy.";
            return false;
        }

        long generation;
        lock (_lifecycleLock)
        {
            generation = ++_mirrorGeneration;
        }
        var check = await CheckAvailabilityAsync();
        if (!check.IsInstalled) { LastError = "scrcpy not installed or not found."; return false; }

        var args = BuildScrcpyArguments(serial, options);
        var process = ToolLauncher.StartLongRunning(_scrcpy, args);

        if (process == null) { LastError = "Failed to start scrcpy process."; return false; }

        // Keep a working mirror until the replacement has survived startup.
        await Task.Delay(500).ConfigureAwait(false);
        if (process.HasExited)
        {
            LastError = "scrcpy process exited immediately.";
            process.Dispose();
            return false;
        }

        System.Diagnostics.Process? replaced = null;
        var superseded = false;
        lock (_lifecycleLock)
        {
            if (generation != _mirrorGeneration)
            {
                superseded = true;
            }
            else
            {
                replaced = _mirrorProcess;
                _mirrorProcess = process;
                MirroredDeviceSerial = serial;
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => OnProcessExited(process);
            }
        }
        if (superseded)
        {
            KillProcess(process);
            LastError = "scrcpy start superseded.";
            return false;
        }
        KillProcess(replaced);
        bool stillCurrent;
        lock (_lifecycleLock) stillCurrent = ReferenceEquals(_mirrorProcess, process) && !process.HasExited;
        if (!stillCurrent)
        {
            LastError = "scrcpy process exited immediately.";
            NotifyStateChanged();
            return false;
        }
        LastError = null;
        NotifyStateChanged();
        return true;
    }

    private string BuildScrcpyArguments(string serial, ScrcpyOptions? options)
    {
        var args = $"-s {serial}";

        if (options != null)
        {
            if (!string.IsNullOrEmpty(options.BitRate) && System.Text.RegularExpressions.Regex.IsMatch(options.BitRate, @"^\d+(\.\d+)?[KMG]?$"))
                args += $" --video-bit-rate={options.BitRate}";

            if (options.MaxFps > 0 && options.MaxFps <= 120)
                args += $" --max-fps={options.MaxFps}";

            if (options.Fullscreen)
            {
                args += " --fullscreen";
            }
            else
            {
                switch (options.WindowPreset)
                {
                    case "Top-Left":
                        var top = WindowsScreenLayout.MirrorBounds(false);
                        args += $" --window-x={top.X} --window-y={top.Y} --window-width={top.Width} --window-height={top.Height}";
                        break;
                    case "Bottom-Right":
                        var bottom = WindowsScreenLayout.MirrorBounds(true);
                        args += $" --window-x={bottom.X} --window-y={bottom.Y} --window-width={bottom.Width} --window-height={bottom.Height}";
                        break;
                    default:
                        if (options.WindowW > 0 && options.WindowH > 0)
                        {
                            args += $" --window-x={options.WindowX} --window-y={options.WindowY} --window-width={options.WindowW} --window-height={options.WindowH}";
                        }
                        break;
                }
            }
        }

        args += $" --window-title \"QA Mirror - {SecurityHelper.HashSerial(serial)}\"";

        return args;
    }

    public void StopMirroring()
    {
        System.Diagnostics.Process? process;
        lock (_lifecycleLock)
        {
            ++_mirrorGeneration;
            process = _mirrorProcess;
            _mirrorProcess = null;
            MirroredDeviceSerial = null;
        }
        KillProcess(process);
        NotifyStateChanged();
    }

    private void OnProcessExited(System.Diagnostics.Process process)
    {
        lock (_lifecycleLock)
        {
            if (!ReferenceEquals(_mirrorProcess, process)) return;
            _mirrorProcess = null;
            MirroredDeviceSerial = null;
        }
        process.Dispose();
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[ScrcpyService] Mirror state observer failed"); }
    }

    private static void KillProcess(System.Diagnostics.Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[ScrcpyService] Mirror operation failed"); }
        finally
        {
            process.Dispose();
        }
    }
}
