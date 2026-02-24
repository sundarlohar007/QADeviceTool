using System.Text.RegularExpressions;
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

        var result = await ProcessRunner.RunAsync(_adb, "version");
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
            status.IsInstalled = false;
            status.StatusMessage = "ADB not found. Place platform-tools in the tools/ folder.";
        }

        return status;
    }

    public async Task<List<DeviceInfo>> GetConnectedDevicesAsync()
    {
        var devices = new List<DeviceInfo>();
        var result = await ProcessRunner.RunAsync(_adb, "devices -l");

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
        var result = await ProcessRunner.RunAsync(_adb, $"-s {serial} shell getprop {property}", 5000);
        return result.Success ? result.Output.Trim() : null;
    }

    public async Task<string> ExecuteCommandAsync(string serial, string args)
    {
        var result = await ProcessRunner.RunAsync(_adb, $"-s {serial} {args}", 15000);
        return result.Output;
    }


    public async Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device)
    {
        device.OsVersion = await GetDevicePropertyAsync(device.Serial, "ro.build.version.release") ?? "Unknown";
        
        var batteryResult = await ProcessRunner.RunAsync(_adb, $"-s {device.Serial} shell dumpsys battery", 5000);
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
        return ProcessRunner.StartLongRunning(_adb, $"-s {serial} logcat -v threadtime");
    }

    public async Task<bool> CaptureScreenshotAsync(string serial, string outputPath)
    {
        var remotePath = "/sdcard/qa_screenshot.png";
        var capResult = await ProcessRunner.RunAsync(_adb, $"-s {serial} shell screencap -p {remotePath}", 15000);
        if (!capResult.Success) return false;

        var pullResult = await ProcessRunner.RunAsync(_adb, $"-s {serial} pull {remotePath} \"{outputPath}\"", 15000);
        await ProcessRunner.RunAsync(_adb, $"-s {serial} shell rm {remotePath}", 5000);

        return pullResult.Success;
    }

    // ─── APK Installation ────────────────────────────────────────
    public async Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? outputCallback = null)
    {
        var result = await ProcessRunner.RunAsync(_adb, $"-s {serial} install -r \"{apkPath}\"", 600000, outputCallback);
        if (result.Success && result.Output.Contains("Success"))
            return (true, "APK installed successfully.");
        return (false, result.Output.Trim());
    }

    // ─── Wireless ADB ────────────────────────────────────────────
    public async Task<(bool Success, string Message)> EnableWirelessAsync(string serial, int port = 5555)
    {
        var result = await ProcessRunner.RunAsync(_adb, $"-s {serial} tcpip {port}", 10000);
        if (!result.Success)
            return (false, $"Failed to enable TCP mode: {result.Output.Trim()}");

        // Get device IP address
        var ipResult = await ProcessRunner.RunAsync(_adb, $"-s {serial} shell ip -f inet addr show wlan0", 5000);
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
        var result = await ProcessRunner.RunAsync(_adb, $"connect {target}", 10000);
        if (result.Success && result.Output.Contains("connected"))
            return (true, $"Connected to {target}");
        return (false, result.Output.Trim());
    }

    public async Task<(bool Success, string Message)> DisconnectWirelessAsync(string ipAddress, int port = 5555)
    {
        var target = $"{ipAddress}:{port}";
        var result = await ProcessRunner.RunAsync(_adb, $"disconnect {target}", 5000);
        return (result.Success, result.Output.Trim());
    }
}
