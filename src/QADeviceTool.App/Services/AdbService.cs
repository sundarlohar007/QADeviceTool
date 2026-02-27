using System.Text.RegularExpressions;
using System.Globalization;
using QADeviceTool.Helpers;
using QADeviceTool.Models;

namespace QADeviceTool.Services;

/// <summary>
/// Wraps ADB commands for device detection, log capture, and screenshots.
/// Uses ToolResolver to find bundled or system ADB.
/// </summary>
public class AdbService
{
    private readonly string _adb;

    public AdbService()
    {
        _adb = ToolResolver.Resolve("adb");
    }

    public async Task<ToolStatus> CheckAvailabilityAsync()
    {
        var status = new ToolStatus
        {
            Name = "ADB (Android Debug Bridge)",
            Description = "Required for Android device communication"
        };

        var result = await ToolLauncher.RunAsync(_adb, "version");
        if (result.Success)
        {
            status.IsInstalled = true;
            var match = Regex.Match(result.Output, @"version ([\d.]+)");
            status.Version = match.Success ? match.Groups[1].Value : "Installed";
            status.Path = ToolResolver.IsBundled(_adb) ? $"Bundled: {_adb}" : (PathHelper.FindInPath("adb") ?? "In PATH");
            status.StatusMessage = "ADB is ready";
        }
        else
        {
            AppLogger.Log.Warn($"[AdbService] CheckAvailabilityAsync failed. Error: {result.Error}, Output: {result.Output}");
            status.IsInstalled = false;
            status.StatusMessage = "ADB not found. Place platform-tools in the tools/ folder.";
        }

        return status;
    }

    public async Task<List<DeviceInfo>> GetConnectedDevicesAsync()
    {
        var devices = new List<DeviceInfo>();
        var result = await ToolLauncher.RunAsync(_adb, "devices -l");

        if (!result.Success) return devices;

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("*")) continue;

            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var stateStr = parts[1];

            var device = new DeviceInfo
            {
                Serial = serial,
                Id = serial,
                Platform = DevicePlatform.Android,
                ConnectionState = stateStr switch
                {
                    "device" => DeviceConnectionState.Online,
                    "unauthorized" => DeviceConnectionState.Unauthorized,
                    _ => DeviceConnectionState.Offline
                }
            };

            foreach (var part in parts.Skip(2))
            {
                if (part.StartsWith("model:"))
                    device.Model = part["model:".Length..].Replace('_', ' ');
                else if (part.StartsWith("device:"))
                    device.Name = part["device:".Length..].Replace('_', ' ');
            }

            if (string.IsNullOrEmpty(device.Model))
                device.Model = await GetDevicePropertyAsync(serial, "ro.product.model") ?? serial;

            devices.Add(device);
        }

        return devices;
    }

    public async Task<string?> GetDevicePropertyAsync(string serial, string property)
    {
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} shell getprop {property}", 5000);
        return result.Success ? result.Output.Trim() : null;
    }

    public async Task<string> ExecuteCommandAsync(string serial, string args)
    {
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} {args}", 15000);
        return result.Output;
    }


    public async Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device)
    {
        device.OsVersion = await GetDevicePropertyAsync(device.Serial, "ro.build.version.release") ?? "Unknown";
        
        var batteryResult = await ToolLauncher.RunAsync(_adb, $"-s {device.Serial} shell dumpsys battery", 5000);
        if (batteryResult.Success)
        {
            var match = Regex.Match(batteryResult.Output, @"level:\s*(\d+)");
            if (match.Success)
                device.BatteryLevel = $"{match.Groups[1].Value}%";
        }

        return device;
    }

    public System.Diagnostics.Process? StartLogCapture(string serial, string outputFilePath)
    {
        return ToolLauncher.StartLongRunning(_adb, $"-s {serial} logcat -v threadtime");
    }

    public async Task<bool> CaptureScreenshotAsync(string serial, string outputPath)
    {
        var remotePath = "/sdcard/qa_screenshot.png";
        var capResult = await ToolLauncher.RunAsync(_adb, $"-s {serial} shell screencap -p {remotePath}", 15000);
        if (!capResult.Success) return false;

        var pullResult = await ToolLauncher.RunAsync(_adb, $"-s {serial} pull {remotePath} \"{outputPath}\"", 15000);
        await ToolLauncher.RunAsync(_adb, $"-s {serial} shell rm {remotePath}", 5000);

        return pullResult.Success;
    }

    /// <summary>
    /// Retrieves the active Process ID (PID) for a given Android package name or partial keyword.
    /// Runs `ps -A -o PID,NAME` and returns the first match's PID.
    /// </summary>
    public async Task<string?> GetPidFromPackageNameAsync(string serial, string packageNameKeyword)
    {
        if (string.IsNullOrWhiteSpace(packageNameKeyword)) return null;
        
        // Fetch all processes: PID  NAME
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} shell ps -A -o PID,NAME", 10000);
        if (!result.Success) return null;

        var lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            // Skip the header
            if (line.Contains("PID") && line.Contains("NAME")) continue;

            // Trim out multiple spaces between columns
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var pid = parts[0];
                var name = parts[1];
                
                // If the app name contains the keyword (e.g. 'com.ubisoft.game' contains 'ubisoft')
                if (name.Contains(packageNameKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    return pid;
                }
            }
        }
        
        return null;
    }

    // ─── APK Installation ────────────────────────────────────────
    public async Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? outputCallback = null)
    {
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} install -r \"{apkPath}\"", 600000, outputCallback);
        if (result.Success && result.Output.Contains("Success"))
            return (true, "APK installed successfully.");
        return (false, result.Output.Trim());
    }

    // ─── Wireless ADB ────────────────────────────────────────────
    public async Task<(bool Success, string Message)> EnableWirelessAsync(string serial, int port = 5555)
    {
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} tcpip {port}", 10000);
        if (!result.Success)
            return (false, $"Failed to enable TCP mode: {result.Output.Trim()}");

        // Get device IP address
        var ipResult = await ToolLauncher.RunAsync(_adb, $"-s {serial} shell ip -f inet addr show wlan0", 5000);
        if (ipResult.Success)
        {
            var match = Regex.Match(ipResult.Output, @"inet (\d+\.\d+\.\d+\.\d+)");
            if (match.Success)
                return (true, match.Groups[1].Value);
        }

        return (true, "TCP mode enabled. Find the device IP in Settings > About Phone > Status.");
    }

    public async Task<(bool Success, string Message)> ConnectWirelessAsync(string ipAddress, int port = 5555)
    {
        var target = $"{ipAddress}:{port}";
        var result = await ToolLauncher.RunAsync(_adb, $"connect {target}", 10000);
        if (result.Success && result.Output.Contains("connected"))
            return (true, $"Connected to {target}");
        return (false, result.Output.Trim());
    }

    public async Task<(bool Success, string Message)> DisconnectWirelessAsync(string ipAddress, int port = 5555)
    {
        var target = $"{ipAddress}:{port}";
        var result = await ToolLauncher.RunAsync(_adb, $"disconnect {target}", 5000);
        return (result.Success, result.Output.Trim());
    }

    // ─── File Explorer ───────────────────────────────────────────
    
    /// <summary>
    /// Lists all files in a specific directory on the Android device.
    /// Parses the raw `adb shell ls -lA` output.
    /// </summary>
    public async Task<List<DeviceFile>> ListDirectoryAsync(string serial, string path)
    {
        var files = new List<DeviceFile>();
        var command = $"-s {serial} shell \"ls -lAL '{path}'\"";
        var result = await ToolLauncher.RunAsync(_adb, command);

        if (!result.Success) return files;

        var lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("total ")) continue; // Skip header

            // Match 'ls -l' output. Example formats:
            // drwxrwx--x 13 root sdcard_rw       4096 2026-02-23 18:24 Android
            // -rw-rw----  1 root sdcard_rw    1234567 2026-02-23 18:24 my_log.txt
            var match = Regex.Match(line, @"^([bcdlps-][rwx-]{9})\s+\d+\s+\w+\s+\w+\s+(\d+)\s+(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})\s+(.+)$");
            
            if (match.Success)
            {
                var permissions = match.Groups[1].Value;
                var sizeStr = match.Groups[2].Value;
                var dateStr = match.Groups[3].Value;
                var name = match.Groups[4].Value;

                // Handle symlinks appropriately
                if (permissions.StartsWith("l") && name.Contains(" -> "))
                {
                    name = name.Substring(0, name.IndexOf(" -> "));
                }

                if (name == "." || name == "..") continue;

                var isDir = permissions.StartsWith("d") || permissions.StartsWith("l");
                long.TryParse(sizeStr, out long size);
                DateTime.TryParseExact(dateStr, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date);

                files.Add(new DeviceFile
                {
                    Name = name,
                    Path = path.TrimEnd('/') + "/" + name,
                    IsDirectory = isDir,
                    Size = isDir ? 0 : size,
                    ModifiedDate = date
                });
            }
        }

        // Sort: Directories first, then alphabetically
        return files.OrderBy(f => !f.IsDirectory).ThenBy(f => f.Name).ToList();
    }

    /// <summary>
    /// Downloads a remote file/folder from the Android device to the local PC.
    /// </summary>
    public async Task<bool> PullFileAsync(string serial, string remotePath, string localDestination)
    {
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} pull \"{remotePath}\" \"{localDestination}\"");
        return result.Success;
    }

    /// <summary>
    /// Uploads a local file/folder from the PC to the Android device.
    /// </summary>
    public async Task<bool> PushFileAsync(string serial, string localPath, string remoteDestination)
    {
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} push \"{localPath}\" \"{remoteDestination}\"");
        return result.Success;
    }

    /// <summary>
    /// Deletes a file or directory from the Android device.
    /// </summary>
    public async Task<bool> DeleteFileAsync(string serial, string remotePath)
    {
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} shell \"rm -rf '{remotePath}'\"");
        return result.Success;
    }

    // ─── App Management ──────────────────────────────────────────

    public async Task<List<AppItem>> ListInstalledAppsAsync(string serial)
    {
        var apps = new List<AppItem>();
        // -3 means list only third-party (user installed) apps
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} shell pm list packages -3", 10000);
        if (!result.Success) return apps;

        var lines = result.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("package:"))
            {
                var pkg = line["package:".Length..].Trim();
                apps.Add(new AppItem { PackageId = pkg, Name = pkg, Platform = DevicePlatform.Android });
            }
        }
        return apps.OrderBy(a => a.Name).ToList();
    }

    public async Task<bool> UninstallAppAsync(string serial, string packageId)
    {
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} uninstall {packageId}", 15000);
        return result.Success && result.Output.Contains("Success");
    }

    public async Task<bool> BroadcastIntentAsync(string serial, string url)
    {
        // am start -a android.intent.action.VIEW -d "scheme://domain/path"
        var result = await ToolLauncher.RunAsync(_adb, $"-s {serial} shell am start -a android.intent.action.VIEW -d \"{url}\"", 10000);
        return result.Success;
    }
}
