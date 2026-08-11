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
    private readonly string _scrcpy;
    private System.Diagnostics.Process? _mirrorProcess;

    public ScrcpyService()
    {
        _scrcpy = ToolResolver.Resolve("scrcpy");
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
            AppLogger.Log.Warn($"[ScrcpyService] CheckAvailabilityAsync failed. Error: {result.Error}, Output: {result.Output}");
            status.IsInstalled = false;
            status.StatusMessage = "scrcpy not found. Place in the tools/ folder.";
        }

        return status;
    }

    public bool IsRunning => _mirrorProcess != null && !_mirrorProcess.HasExited;

    public string? MirroredDeviceSerial { get; private set; }

    public string? LastError { get; private set; }

    public async Task<bool> StartMirroringAsync(string serial, ScrcpyOptions? options = null)
    {
        // Stop any existing mirroring before starting a new one
        if (_mirrorProcess != null)
        {
            StopMirroring();
        }

        var check = await CheckAvailabilityAsync();
        if (!check.IsInstalled) { LastError = "scrcpy not installed or not found."; return false; }

        var args = BuildScrcpyArguments(serial, options);
        _mirrorProcess = ToolLauncher.StartLongRunning(_scrcpy, args);

        if (_mirrorProcess == null) { LastError = "Failed to start scrcpy process."; return false; }

        MirroredDeviceSerial = serial;

        // Wait briefly to see if process starts and stays running
        await Task.Delay(500);

        LastError = "scrcpy process exited immediately.";
        if (_mirrorProcess.HasExited)
        {
            MirroredDeviceSerial = null;
            _mirrorProcess.Dispose();
            _mirrorProcess = null;
            return false;
        }

        LastError = null;


        return true;


    }

    private string BuildScrcpyArguments(string serial, ScrcpyOptions? options)
    {
        var args = $"-s {serial}";

        if (options != null)
        {
            if (!string.IsNullOrEmpty(options.BitRate) && options.BitRate != "2M"
                && System.Text.RegularExpressions.Regex.IsMatch(options.BitRate, @"^\d+(\.\d+)?[KMG]?$"))
                args += $" --bit-rate={options.BitRate}";

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
                        args += " --window-x=0 --window-y=0 --window-width=1080 --window-height=1920";
                        break;
                    case "Bottom-Right":
                        args += " --window-x=960 --window-y=540 --window-width=960 --window-height=1080";
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

        args += $" --window-title \"QA Mirror - {serial}\"";

        return args;
    }

    public void StopMirroring()
    {
        if (_mirrorProcess == null) return;

        try
        {
            if (!_mirrorProcess.HasExited)
            {
                _mirrorProcess.Kill(true);
            }
        }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[ScrcpyService] Mirror operation failed"); }
        finally
        {
            _mirrorProcess?.Dispose();
            _mirrorProcess = null;
            MirroredDeviceSerial = null;
        }
    }
}
