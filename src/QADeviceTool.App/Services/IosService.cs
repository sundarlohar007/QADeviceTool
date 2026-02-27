using System.Text.RegularExpressions;
using QADeviceTool.Helpers;
using QADeviceTool.Models;
using System.IO;

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
    private readonly string _afcClient;

    public IosService()
    {
        _ideviceId = "idevice_id.exe";
        _ideviceInfo = "ideviceinfo.exe";
        _ideviceSyslog = "idevicesyslog.exe";
        _ideviceScreenshot = "idevicescreenshot.exe";
        _ideviceInstaller = "ideviceinstaller.exe";
        _afcClient = "afcclient.exe";
    }

    public async Task<ToolStatus> CheckAvailabilityAsync()
    {
        var status = new ToolStatus
        {
            Name = "libimobiledevice (iOS Tools)",
            Description = "Required for iOS device communication"
        };

        var result = await ToolLauncher.RunAsync(_ideviceId, "-l");
        if (result.Success || result.ExitCode == 0)
        {
            status.IsInstalled = true;
            status.Version = "Installed";
            status.Path = ToolLauncher.ToolsDirectory;
            status.StatusMessage = "iOS tools are ready";
        }
        else
        {
            // idevice_id -l fails when Apple Mobile Device Service is not running.
            // Check if the executable file actually exists on disk (bundled or in PATH).
            bool fileExists = System.IO.File.Exists(System.IO.Path.Combine(ToolLauncher.ToolsDirectory, _ideviceId));
            if (fileExists)
            {
                status.IsInstalled = true;
                status.Version = "Installed (driver not active)";
                status.Path = ToolLauncher.ToolsDirectory;
                status.StatusMessage = "iOS tools found, but Apple Mobile Device Service is not running. Install or launch iTunes to enable iOS device support.";
            }
            else
            {
                status.IsInstalled = false;
                status.StatusMessage = "libimobiledevice not found. Place tools in the tools/ folder.";
            }
        }

        return status;
    }

    public async Task<List<DeviceInfo>> GetConnectedDevicesAsync()
    {
        var devices = new List<DeviceInfo>();
        var result = await ToolLauncher.RunAsync(_ideviceId, "-l");

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
        var result = await ToolLauncher.RunAsync(_ideviceInfo, $"-u {device.Serial}", 10000);
        if (!result.Success)
        {
            // If ideviceinfo fails, it's often because the device hasn't trusted the PC yet
            var errorOut = result.Error + result.Output;
            if (errorOut.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || 
                errorOut.Contains("Lockdown", StringComparison.OrdinalIgnoreCase) ||
                errorOut.Contains("Could not connect", StringComparison.OrdinalIgnoreCase))
            {
                device.ConnectionState = DeviceConnectionState.PendingTrust;
            }
            return device;
        }

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
        return ToolLauncher.StartLongRunning(_ideviceSyslog, $"-u {udid}");
    }

    public async Task<bool> CaptureScreenshotAsync(string udid, string outputPath)
    {
        var result = await ToolLauncher.RunAsync(_ideviceScreenshot, $"-u {udid} \"{outputPath}\"", 15000);
        return result.Success;
    }

    // ─── IPA Installation ────────────────────────────────────────
    public async Task<(bool Success, string Message)> InstallIpaAsync(string udid, string ipaPath, Action<string>? outputCallback = null)
    {
        // Sanitize path for MSYS environment (which ideviceinstaller uses under the hood on Windows)
        string sanitizedPath = ipaPath.Replace("\\", "/");
        var result = await ToolLauncher.RunAsync(_ideviceInstaller, $"-u {udid} -i \"{sanitizedPath}\"", 600000, outputCallback);

        if (result.Success)
            return (true, "IPA installed successfully.");

        // ideviceinstaller sometimes outputs errors to stdout instead of stderr
        string error = !string.IsNullOrWhiteSpace(result.Error) ? result.Error : result.Output;
        return (false, $"Failed to install IPA. Error: {error.Trim()}");
    }

    public async Task<List<AppItem>> ListInstalledAppsAsync(string udid)
    {
        var apps = new List<AppItem>();
        var result = await ToolLauncher.RunAsync(_ideviceInstaller, $"-u {udid} -l", 15000);
        if (!result.Success) return apps;

        var lines = result.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1)) // Skip header: CFBundleIdentifier, CFBundleVersion, ...
        {
            var parts = line.Split(',', 3);
            if (parts.Length > 0 && !line.StartsWith("CFBundleIdentifier"))
            {
                var pkg = parts[0].Trim();
                var ver = parts.Length > 1 ? parts[1].Trim(' ', '"') : "";
                var name = parts.Length > 2 ? parts[2].Trim(' ', '"') : pkg;
                
                apps.Add(new AppItem { PackageId = pkg, Name = name, Version = ver, Platform = DevicePlatform.iOS });
            }
        }
        return apps.OrderBy(a => a.Name).ToList();
    }

    public async Task<bool> UninstallAppAsync(string udid, string packageId)
    {
        var result = await ToolLauncher.RunAsync(_ideviceInstaller, $"-u {udid} -U {packageId}", 20000);
        return result.Success && result.Output.Contains("Complete");
    }

    // ─── File Explorer (AFC) ──────────────────────────────────────
    public async Task<List<DeviceFile>> ListDirectoryAsync(string udid, string path)
    {
        var files = new List<DeviceFile>();
        var result = await ToolLauncher.RunAsync(_afcClient, $"-u {udid} ls -l \"{path}\"");

        if (!result.Success) return files;

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Clean ANSI escape codes if any (cprintf fallback)
        var ansiRegex = new Regex(@"\x1B\[[^a-zA-Z]*[a-zA-Z]");

        foreach (var rline in lines)
        {
            var line = ansiRegex.Replace(rline.TrimEnd('\r'), "");
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 10)
            {
                var mode = parts[0];
                long.TryParse(parts[4], out var size);
                var name = string.Join(" ", parts.Skip(9));

                files.Add(new DeviceFile
                {
                    Name = name,
                    Path = path == "/" ? $"/{name}" : $"{path}/{name}",
                    IsDirectory = mode.StartsWith("d"),
                    Size = size
                });
            }
        }

        return files.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name).ToList();
    }

    public async Task<bool> PullFileAsync(string udid, string remotePath, string localPath)
    {
        var result = await ToolLauncher.RunAsync(_afcClient, $"-u {udid} get \"{remotePath}\" \"{localPath}\"", 60000);
        return result.Success && File.Exists(localPath);
    }

    public async Task<bool> PushFileAsync(string udid, string localPath, string remotePath)
    {
        var result = await ToolLauncher.RunAsync(_afcClient, $"-u {udid} put \"{localPath}\" \"{remotePath}\"", 60000);
        return result.Success;
    }

    public async Task<bool> DeleteFileAsync(string udid, string path)
    {
        var result = await ToolLauncher.RunAsync(_afcClient, $"-u {udid} rm -rf \"{path}\"", 10000);
        return result.Success;
    }
}
