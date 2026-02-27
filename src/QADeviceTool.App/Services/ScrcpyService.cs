using System.Text.RegularExpressions;
using QADeviceTool.Helpers;
using QADeviceTool.Models;

namespace QADeviceTool.Services;

/// <summary>
/// Controls scrcpy for Android screen mirroring.
/// Uses ToolResolver to find bundled or system scrcpy.
/// </summary>
public class ScrcpyService
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

        var result = await ToolLauncher.RunAsync(_scrcpy, "--version");
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

    public async Task<bool> StartMirroringAsync(string serial)
    {
        if (IsRunning) return true;

        var check = await CheckAvailabilityAsync();
        if (!check.IsInstalled) return false;

        _mirrorProcess = ToolLauncher.StartLongRunning(_scrcpy, $"-s {serial} --window-title \"QA Mirror - {serial}\"");
        
        await Task.Delay(500);
        
        return _mirrorProcess != null && !_mirrorProcess.HasExited;
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
        catch { }
        finally
        {
            _mirrorProcess?.Dispose();
            _mirrorProcess = null;
        }
    }
}
