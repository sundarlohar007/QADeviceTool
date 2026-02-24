using System.Text.RegularExpressions;
using QADeviceTool.Helpers;
using QADeviceTool.Models;

namespace QADeviceTool.Services;

/// <summary>
/// Wraps libimobiledevice commands for iOS device detection, log capture, and screenshots.
/// Uses ToolResolver to find bundled or system tools.
/// </summary>
public class IosService
{
    private readonly string _ideviceId;
    private readonly string _ideviceInfo;
    private readonly string _ideviceSyslog;
    private readonly string _ideviceScreenshot;
    private readonly string _ideviceInstaller;

    public IosService()
    {
        _ideviceId = ToolResolver.Resolve("idevice_id");
        _ideviceInfo = ToolResolver.Resolve("ideviceinfo");
        _ideviceSyslog = ToolResolver.Resolve("idevicesyslog");
        _ideviceScreenshot = ToolResolver.Resolve("idevicescreenshot");
        _ideviceInstaller = ToolResolver.Resolve("ideviceinstaller");
    }

    public async Task<ToolStatus> CheckAvailabilityAsync()
    {
        var status = new ToolStatus
        {
            Name = "libimobiledevice (iOS Tools)",
            Description = "Required for iOS device communication"
        };

        var result = await ProcessRunner.RunAsync(_ideviceId, "-l");
        if (result.Success || result.ExitCode == 0)
        {
            status.IsInstalled = true;
            status.Version = "Installed";
            status.Path = ToolResolver.IsBundled(_ideviceId) ? $"Bundled: {_ideviceId}" : (PathHelper.FindInPath("idevice_id") ?? "In PATH");
            status.StatusMessage = "iOS tools are ready";
        }
        else
        {
            status.IsInstalled = false;
            status.StatusMessage = "libimobiledevice not found. Place tools in the tools/ folder.";
        }

        return status;
    }

    public async Task<List<DeviceInfo>> GetConnectedDevicesAsync()
    {
        var devices = new List<DeviceInfo>();
        var result = await ProcessRunner.RunAsync(_ideviceId, "-l");

        if (!result.Success) return devices;

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var udid = line.Trim();
            if (string.IsNullOrEmpty(udid)) continue;

            var device = new DeviceInfo
            {
                Serial = udid,
                Id = udid,
                Platform = DevicePlatform.iOS,
                ConnectionState = DeviceConnectionState.Online
            };

            device = await GetDeviceDetailsAsync(device);
            devices.Add(device);
        }

        return devices;
    }

    public async Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device)
    {
        var result = await ProcessRunner.RunAsync(_ideviceInfo, $"-u {device.Serial}", 10000);
        if (!result.Success) return device;

        var lines = result.Output.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("DeviceName:"))
                device.Name = trimmed["DeviceName:".Length..].Trim();
            else if (trimmed.StartsWith("ProductType:"))
                device.Model = trimmed["ProductType:".Length..].Trim();
            else if (trimmed.StartsWith("ProductVersion:"))
                device.OsVersion = trimmed["ProductVersion:".Length..].Trim();
            else if (trimmed.StartsWith("BatteryCurrentCapacity:"))
                device.BatteryLevel = trimmed["BatteryCurrentCapacity:".Length..].Trim() + "%";
        }

        if (string.IsNullOrEmpty(device.Model))
            device.Model = "iOS Device";

        return device;
    }

    public System.Diagnostics.Process? StartLogCapture(string udid, string outputFilePath)
    {
        return ProcessRunner.StartLongRunning(_ideviceSyslog, $"-u {udid}");
    }

    public async Task<bool> CaptureScreenshotAsync(string udid, string outputPath)
    {
        var result = await ProcessRunner.RunAsync(_ideviceScreenshot, $"-u {udid} \"{outputPath}\"", 15000);
        return result.Success;
    }

    // ─── IPA Installation ────────────────────────────────────────
    public async Task<(bool Success, string Message)> InstallIpaAsync(string udid, string ipaPath, Action<string>? outputCallback = null)
    {
        // Sanitize path for MSYS environment (which ideviceinstaller uses under the hood on Windows)
        string sanitizedPath = ipaPath.Replace("\\", "/");
        
        var result = await ProcessRunner.RunAsync(_ideviceInstaller, $"-u {udid} -i \"{sanitizedPath}\"", 600000, outputCallback);

        if (result.Success)
            return (true, "IPA installed successfully.");

        // ideviceinstaller sometimes outputs errors to stdout instead of stderr
        string error = !string.IsNullOrWhiteSpace(result.Error) ? result.Error : result.Output;
        return (false, $"Failed to install IPA. Error: {error.Trim()}");
    }
}
