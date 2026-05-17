using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Wraps ADB commands for device detection, log capture, and screenshots.
/// Uses ToolResolver to find bundled or system ADB.
/// All commands are serialized via semaphore to prevent concurrent USB transport access.
/// </summary>
public class AdbService : IAdbService
{
    private readonly string _adb;
    private static readonly SemaphoreSlim _adbLock = new(4, 4);
    
    private const int DefaultTimeoutMs = 8000;
    private const int FastTimeoutMs = 5000;
    private const int MaxRetryAttempts = 2;
    private const int RetryDelayMs = 500;

    public AdbService()
    {
        _adb = ToolResolver.Resolve("adb");
    }

    // ─── Semaphore-guarded ADB execution ─────────────────────────
    // All adb calls go through these to prevent concurrent USB transport access.

    private async Task<ToolLauncherResult> RunAdbAsync(string arguments, int timeoutMs = DefaultTimeoutMs, Action<string>? outputCallback = null)
    {
        return await RunAdbWithRetryAsync(arguments, timeoutMs, outputCallback);
    }

    private async Task<ToolLauncherResult> RunAdbWithRetryAsync(string arguments, int timeoutMs, Action<string>? outputCallback, int attempt = 1)
    {
        await _adbLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ToolLauncherResult? result = null;
            for (int retry = 0; retry < MaxRetryAttempts; retry++)
            {
                result = await ToolLauncher.RunAsync(_adb, arguments, timeoutMs, outputCallback).ConfigureAwait(false);
                if (result.Success) return result;

                // Only retry on transient failures, not permanent errors
                if (result.Error.Contains("unauthorized") || result.Error.Contains("Failure") ||
                    result.Output.Contains("Failure") || result.Output.Contains("Error:") ||
                    result.ExitCode == 1) break;

                if (retry < MaxRetryAttempts - 1)
                    await Task.Delay(RetryDelayMs).ConfigureAwait(false);
            }

            return result!;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] Exception in RunAdbAsync: {ex.Message}");
            return new ToolLauncherResult { Success = false, Error = ex.Message };
        }
        finally
        {
            _adbLock.Release();
        }
    }

    private async Task<System.Diagnostics.Process?> StartAdbLongRunning(string arguments)
    {
        await _adbLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return ToolLauncher.StartLongRunning(_adb, arguments);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[AdbService] Failed to start long-running ADB process");
            return null;
        }
        finally
        {
            _adbLock.Release();
        }
    }

    public async Task<ToolStatus> CheckAvailabilityAsync()
    {
        var status = new ToolStatus
        {
            Name = "ADB (Android Debug Bridge)",
            Description = "Required for Android device communication"
        };

        var result = await RunAdbAsync("version", FastTimeoutMs);
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
        
        try
        {
            var result = await RunAdbAsync("devices -l", DefaultTimeoutMs);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                AppLogger.Log.Debug("[AdbService] No devices found or ADB command failed");
                return devices;
            }

            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Skip(1))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("*")) continue;

                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var serial = parts[0];
                var stateStr = parts[1];

                var connectionState = stateStr switch
                {
                    "device" => DeviceConnectionState.Online,
                    "unauthorized" => DeviceConnectionState.Unauthorized,
                    "offline" => DeviceConnectionState.Offline,
                    "no device" => DeviceConnectionState.Offline,
                    _ => DeviceConnectionState.Offline
                };

                var device = new DeviceInfo
                {
                    Serial = serial,
                    Id = serial,
                    Platform = DevicePlatform.Android,
                    ConnectionState = connectionState
                };

                foreach (var part in parts.Skip(2))
                {
                    if (part.StartsWith("model:"))
                        device.Model = part["model:".Length..].Replace('_', ' ');
                    else if (part.StartsWith("device:"))
                        device.Name = part["device:".Length..].Replace('_', ' ');
                    else if (part.StartsWith("product:"))
                        device.Product = part["product:".Length..];
                    else if (part.StartsWith("usb:"))
                        device.UsbInfo = part["usb:".Length..];
                }

                if (connectionState == DeviceConnectionState.Online)
                {
                    if (string.IsNullOrEmpty(device.Model))
                        device.Model = await GetDevicePropertySafeAsync(serial, "ro.product.model") ?? serial;
                }
                else
                {
                    device.Model = $"[{stateStr.ToUpper()}] {serial}";
                }

                devices.Add(device);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[AdbService] Exception in GetConnectedDevicesAsync");
        }

        return devices;
    }

    private async Task<string?> GetDevicePropertySafeAsync(string serial, string property)
    {
        try
        {
            var result = await RunAdbAsync($"-s {serial} shell getprop {property}", FastTimeoutMs);
            return result.Success ? result.Output.Trim() : null;
        }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[AdbService] GetConnectedDevicesAsync failed"); return null; }
    }

    public async Task<string?> GetDevicePropertyAsync(string serial, string property)
    {
        var result = await RunAdbAsync($"-s {serial} shell getprop {property}", FastTimeoutMs);
        return result.Success ? result.Output.Trim() : null;
    }

    public async Task<(bool Success, string Output, string Error)> ExecuteCommandWithResultAsync(string serial, string args)
    {
        var result = await RunAdbAsync($"-s {serial} {args}", DefaultTimeoutMs);
        return (result.Success, result.Output, result.Error);
    }


    public async Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device)
    {
        if (device.ConnectionState != DeviceConnectionState.Online)
        {
            AppLogger.Log.Debug($"[AdbService] Skipping details for {device.Serial} - device is {device.ConnectionState}");
            return device;
        }

        try
        {
            device.OsVersion = await GetDevicePropertyAsync(device.Serial, "ro.build.version.release") ?? "Unknown";
            
            var batteryResult = await RunAdbAsync($"-s {device.Serial} shell dumpsys battery", FastTimeoutMs);
            if (batteryResult.Success)
            {
                var match = Regex.Match(batteryResult.Output, @"level:\s*(\d+)");
                if (match.Success)
                    device.BatteryLevel = $"{match.Groups[1].Value}%";
                
                var matchStatus = Regex.Match(batteryResult.Output, @"status:\s*(\w+)");
                if (matchStatus.Success)
                    device.BatteryStatus = matchStatus.Groups[1].Value;
            }

            var manufacturer = await GetDevicePropertyAsync(device.Serial, "ro.product.manufacturer");
            if (!string.IsNullOrEmpty(manufacturer))
                device.Manufacturer = manufacturer;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] Error getting details for {device.Serial}");
        }

        return device;
    }

    public async Task<System.Diagnostics.Process?> StartLogCaptureAsync(string serial, string outputFilePath,
        LogcatBuffer buffer = LogcatBuffer.Main, LogcatFormat format = LogcatFormat.ThreadTime)
    {
        var bufferArg = buffer switch
        {
            LogcatBuffer.Main => "-b main",
            LogcatBuffer.System => "-b system",
            LogcatBuffer.Events => "-b events",
            LogcatBuffer.Crash => "-b crash",
            LogcatBuffer.Radio => "-b radio",
            _ => "-b main"
        };

        var formatArg = format switch
        {
            LogcatFormat.Brief => "brief",
            LogcatFormat.Process => "process",
            LogcatFormat.Tag => "tag",
            LogcatFormat.Thread => "thread",
            LogcatFormat.Time => "time",
            LogcatFormat.ThreadTime => "threadtime",
            LogcatFormat.Long => "long",
            LogcatFormat.Raw => "raw",
            _ => "threadtime"
        };

        return await StartAdbLongRunning($"-s {serial} logcat {bufferArg} -v {formatArg}").ConfigureAwait(false);
    }

    public async Task<bool> CaptureScreenshotAsync(string serial, string outputPath)
    {
        if (string.IsNullOrEmpty(serial))
        {
            AppLogger.Log.Warn("[AdbService] CaptureScreenshotAsync called with empty serial");
            return false;
        }

        try
        {
            var remotePath = "/sdcard/qa_screenshot.png";
            var capResult = await RunAdbAsync($"-s {serial} shell screencap -p {remotePath}", DefaultTimeoutMs);
            if (!capResult.Success) return false;

            var pullResult = await RunAdbAsync($"-s {serial} pull {remotePath} \"{outputPath}\"", DefaultTimeoutMs);
            await RunAdbAsync($"-s {serial} shell rm {remotePath}", FastTimeoutMs);

            return pullResult.Success;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] Screenshot failed for {serial}");
            return false;
        }
    }

    /// <summary>
    /// Starts screen recording on device. Returns the remote path if started, null if failed.
    /// Recording runs until StopScreenRecord is called or max duration reached.
    /// </summary>
    public async Task<string?> StartScreenRecordAsync(string serial, string? outputDir = null, int maxDurationSec = 180, string bitRate = "8M")
    {
        if (string.IsNullOrWhiteSpace(serial))
            return null;

        // Prevent concurrent recordings on the same AdbService instance
        if (_activeRecordProcess != null && !_activeRecordProcess.HasExited)
            return null;

        try
        {
            _activeRecordRemotePath = $"/sdcard/qa_screenrecord_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            var remotePath = _activeRecordRemotePath;
            var arguments = $"-s {serial} shell screenrecord --bit-rate {bitRate} --time-limit {maxDurationSec} {remotePath}";
            var process = await StartAdbLongRunning(arguments);
            if (process == null) return null;

            // Store process reference for later stop
            _activeRecordProcess = process;
            ProcessManagerService.TrackProcess(process);
            return remotePath;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] StartScreenRecord failed for {serial}");
            return null;
        }
    }

    /// <summary>
    /// Stops the active screen recording and pulls the video file to local storage.
    /// </summary>
    public async Task<string?> StopScreenRecordAsync(string serial, string? localOutputPath = null)
    {
        try
        {
            var process = Interlocked.Exchange(ref _activeRecordProcess, null);

            if (process != null && !process.HasExited)
            {
                // Send SIGINT (2) to screenrecord on the device to gracefully finalize the MP4 header.
                // On Android, kill -2 sends SIGINT which makes screenrecord write the MP4 trailer.
                try
                {
                    var pidResult = await RunAdbAsync($"-s {serial} shell pidof screenrecord", FastTimeoutMs);
                    if (pidResult.Success && !string.IsNullOrWhiteSpace(pidResult.Output))
                    {
                        var pid = pidResult.Output.Trim();
                        await RunAdbAsync($"-s {serial} shell kill -2 {pid}", FastTimeoutMs);
                        await Task.Delay(500); // wait for mp4 finalization
                    }
                }
        catch (Exception ex) { AppLogger.Log.Warn(ex, "[AdbService] StopScreenRecord failed"); }
        try { process.Kill(false); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[AdbService] ScreenRecord kill error"); }
                process.Dispose();
            }

            // Find the remote file to pull — screenrecord saves to last specified path
            // We need to pull the most recent screenrecord file
            var listResult = await RunAdbAsync($"-s {serial} shell ls -t /sdcard/qa_screenrecord_*.mp4", FastTimeoutMs);
            if (!listResult.Success || string.IsNullOrWhiteSpace(listResult.Output))
                return null;

            var remoteFile = listResult.Output.Trim().Split('\n', '\r')[0].Trim();
            var localPath = localOutputPath ?? Path.Combine(
                Helpers.PathHelper.GetDefaultSessionsDirectory(),
                $"screenrecord_{serial}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

            var pullResult = await RunAdbAsync($"-s {serial} pull \"{remoteFile}\" \"{localPath}\"", 30000);
            // Clean up remote file
            await RunAdbAsync($"-s {serial} shell rm \"{remoteFile}\"", FastTimeoutMs);

            return pullResult.Success ? localPath : null;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] StopScreenRecord failed for {serial}");
            return null;
        }
    }

    public bool IsScreenRecording { get { var p = _activeRecordProcess; return p != null && !p.HasExited; } }
    private System.Diagnostics.Process? _activeRecordProcess;
    private string? _activeRecordRemotePath;

    public async Task<string?> GetPidFromPackageNameAsync(string serial, string packageNameKeyword)
    {
        if (string.IsNullOrWhiteSpace(packageNameKeyword)) return null;
        
        try
        {
            var result = await RunAdbAsync($"-s {serial} shell ps -A -o PID,NAME", 10000);
            if (!result.Success) return null;

            var lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Contains("PID") && line.Contains("NAME")) continue;

                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var pid = parts[0];
                    var name = parts[1];
                    
                    if (name.Contains(packageNameKeyword, StringComparison.OrdinalIgnoreCase))
                        return pid;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] GetPidFromPackageNameAsync failed for {packageNameKeyword}");
        }
        
        return null;
    }

    public async Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? outputCallback = null)
    {
        var result = await RunAdbAsync($"-s {serial} install -r \"{apkPath}\"", 600000, outputCallback);
        // Check last line of output for "Success" — avoids false positives from package names containing "Success"
        var lastLine = result.Output.Trim().Split('\\n').LastOrDefault() ?? "";
        if (result.Success && lastLine.Trim().Equals("Success", StringComparison.OrdinalIgnoreCase))
            return (true, "APK installed successfully.");
        return (false, result.Output.Trim());
    }

    public async Task<(bool Success, string Message)> EnableWirelessAsync(string serial, int port = 5555)
    {
        var result = await RunAdbAsync($"-s {serial} tcpip {port}", 10000);
        if (!result.Success)
            return (false, $"Failed to enable TCP mode: {result.Output.Trim()}");

        var ipResult = await RunAdbAsync($"-s {serial} shell ip -f inet addr show wlan0", FastTimeoutMs);
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
        var result = await RunAdbAsync($"connect {target}", 10000);
        if (result.Success && result.Output.Contains("connected"))
            return (true, $"Connected to {target}");
        return (false, result.Output.Trim());
    }

    public async Task<(bool Success, string Message)> DisconnectWirelessAsync(string ipAddress, int port = 5555)
    {
        var target = $"{ipAddress}:{port}";
        var result = await RunAdbAsync($"disconnect {target}", FastTimeoutMs);
        return (result.Success, result.Output.Trim());
    }

    public async Task<List<DeviceFile>> ListDirectoryAsync(string serial, string path)
    {
        try
        {
            if (!IsSafePath(path)) return new List<DeviceFile>();
            var safePath = path.Replace("'", "'\\''");
            var command = $"-s {serial} shell \"ls -lAL '{safePath}'\"";
            var result = await RunAdbAsync(command, DefaultTimeoutMs);
            if (result.Success)
            {
                var parsed = ParseAndroidLsListing(result.Output, path);
                if (parsed.Count > 0)
                    return parsed;
            }

            // Detect permission denied on restricted paths like /data/
            var fallback = await RunAdbAsync($"-s {serial} shell \"ls -1Ap '{safePath}'\"", DefaultTimeoutMs);
            return fallback.Success ? ParseSimpleDirectoryListing(fallback.Output, path) : new List<DeviceFile>(); // Permission denied returns empty listing
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] ListDirectoryAsync failed for {path}");
        }

        return new List<DeviceFile>();
    }

    internal static List<DeviceFile> ParseAndroidLsListing(string output, string parentPath)
    {
        var files = new List<DeviceFile>();
        var basePath = NormalizeDevicePath(parentPath);
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("total ")) continue;

            var match = Regex.Match(line,
                @"^(?<perm>[bcdlps-][rwx-]{9})\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(?::\d{2})?)\s+(?<name>.+)$");

            if (!match.Success) continue;

            var permissions = match.Groups["perm"].Value;
            var sizeStr = match.Groups["size"].Value;
            var dateStr = match.Groups["date"].Value;
            var name = match.Groups["name"].Value;

            if (permissions.StartsWith("l") && name.Contains(" -> "))
                name = name[..name.IndexOf(" -> ", StringComparison.Ordinal)];

            if (name == "." || name == "..") continue;

            var isDir = permissions.StartsWith("d") || permissions.StartsWith("l");
            long.TryParse(sizeStr, out var size);
            var date = ParseAndroidLsDate(dateStr);

            files.Add(new DeviceFile
            {
                Name = name,
                Path = CombineDevicePath(basePath, name),
                IsDirectory = isDir,
                Size = isDir ? 0 : size,
                ModifiedDate = date
            });
        }

        return SortDeviceFiles(files);
    }

    internal static List<DeviceFile> ParseSimpleDirectoryListing(string output, string parentPath)
    {
        var files = new List<DeviceFile>();
        var basePath = NormalizeDevicePath(parentPath);
        foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") continue;

            var isDir = name.EndsWith("/");
            name = name.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(name)) continue;

            files.Add(new DeviceFile
            {
                Name = name,
                Path = CombineDevicePath(basePath, name),
                IsDirectory = isDir,
                Size = 0,
                ModifiedDate = DateTime.MinValue
            });
        }

        return SortDeviceFiles(files);
    }

    private static DateTime ParseAndroidLsDate(string dateStr)
    {
        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" };
        return DateTime.TryParseExact(dateStr, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateTime.MinValue;
    }

    private static List<DeviceFile> SortDeviceFiles(IEnumerable<DeviceFile> files)
        => files.OrderBy(f => !f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static string NormalizeDevicePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        normalized = normalized.TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? "/" : normalized;
    }

    private static string CombineDevicePath(string parentPath, string name)
    {
        var basePath = NormalizeDevicePath(parentPath);
        return basePath == "/" ? $"/{name}" : $"{basePath}/{name}";
    }

    public async Task<bool> PullFileAsync(string serial, string remotePath, string localDestination)
    {
        var result = await RunAdbAsync($"-s {serial} pull \"{remotePath}\" \"{localDestination}\"");
        return result.Success;
    }

    public async Task<bool> PushFileAsync(string serial, string localPath, string remoteDestination)
    {
        var result = await RunAdbAsync($"-s {serial} push \"{localPath}\" \"{remoteDestination}\"");
        return result.Success;
    }

    public async Task<bool> DeleteFileAsync(string serial, string remotePath)
    {
        if (!IsSafePath(remotePath)) return false;
        var safePath = remotePath.Replace("'", "'\\''");
        var result = await RunAdbAsync($"-s {serial} shell \"rm -rf '{safePath}'\"");
        return result.Success;
    }

    private static bool IsSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("..")) return false;
        if (path.Contains("$((") || path.Contains("$( ") || path.Contains("`")) return false;
        if (path.Contains(';')) return false;
        return true;
    }

    public async Task<List<AppItem>> ListInstalledAppsAsync(string serial)
    {
        var apps = new List<AppItem>();
        
        try
        {
            var result = await RunAdbAsync($"-s {serial} shell pm list packages -3", 15000);
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
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] ListInstalledAppsAsync failed for {serial}");
        }

        return apps.OrderBy(a => a.Name).ToList();
    }

    public async Task<bool> UninstallAppAsync(string serial, string packageId)
    {
        if (!IsValidPackageName(packageId)) return false;
        var result = await RunAdbAsync($"-s {serial} uninstall {packageId}", DefaultTimeoutMs);
        return result.Success && result.Output.Contains("Success");
    }

    private static bool IsValidPackageName(string packageId)
    {
        return !string.IsNullOrEmpty(packageId)
            && System.Text.RegularExpressions.Regex.IsMatch(packageId, @"^[a-zA-Z0-9._]+$");
    }

    public async Task<bool> ForceStopAppAsync(string serial, string packageId)
    {
        if (!IsValidPackageName(packageId)) return false;
        var result = await RunAdbAsync($"-s {serial} shell am force-stop {packageId}", FastTimeoutMs);
        return result.Success;
    }

    public async Task<bool> ClearAppDataAsync(string serial, string packageId)
    {
        if (!IsValidPackageName(packageId)) return false;
        var result = await RunAdbAsync($"-s {serial} shell pm clear {packageId}", 15000);
        return result.Success && result.Output.Contains("Success");
    }

    public async Task<string> GetAppDetailsAsync(string serial, string packageId)
    {
        if (!IsValidPackageName(packageId)) return "Invalid package name.";
        var result = await RunAdbAsync($"-s {serial} shell dumpsys package {packageId}", DefaultTimeoutMs);
        return result.Success ? result.Output : "Failed to retrieve app details.";
    }

    public async Task<bool> SetDeviceClipboardAsync(string serial, string text)
    public async Task<bool> SetDeviceClipboardAsync(string serial, string text)
    {
        // Escape single quotes for Android shell to prevent injection
        var escaped = text.Replace("\\", "\\\\").Replace("'", "\\'");
        var result = await RunAdbAsync(
            $"-s {serial} shell cmd clipboard set '{escaped}'", FastTimeoutMs);
        return result.Success;
    }
    public async Task<string> GetDeviceClipboardAsync(string serial)
    {
        var result = await RunAdbAsync($"-s {serial} shell dumpsys clipboard", FastTimeoutMs);
        return result.Success ? result.Output : "Failed to read clipboard.";
    }

    public async Task<bool> SendNotificationAsync(string serial, string title, string body, string? channel = null)
    {
        var tag = $"logpro_{DateTime.Now.Ticks}";
        var channelId = channel ?? "default";
        if (!System.Text.RegularExpressions.Regex.IsMatch(channelId, @"^[a-zA-Z0-9._\-]+$"))
            channelId = "default";
        var safeTitle = title.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");
        var safeBody = body.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");
        var result = await RunAdbAsync($\"-s {serial} shell cmd notification post -t \"{safeTitle}\" \"{safeBody}\" --channel {channelId} {tag}\", FastTimeoutMs);
        return result.Success;
    }
        var combined = $"{result.Output}\n{result.Error}";
        var started = combined.Contains("Starting: Intent", StringComparison.OrdinalIgnoreCase);
        return result.Success && started
            && !combined.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            && !combined.Contains("unable to resolve Intent", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryBuildDeepLinkIntentArgs(string serial, string url, out string args)
    {
        args = string.Empty;
        if (!Regex.IsMatch(serial, @"^[a-zA-Z0-9._:\-]+$"))
            return false;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        var trimmed = url.Trim();
        if (trimmed.Contains('\r') || trimmed.Contains('\n') || trimmed.Contains('`') || trimmed.Contains("$("))
            return false;

        var isIntentUri = trimmed.StartsWith("intent:", StringComparison.OrdinalIgnoreCase);
        if (!isIntentUri && !Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return false;

        var safeUrl = EscapeSingleQuotedShell(trimmed);
        args = isIntentUri
            ? $"-s {serial} shell am start -W '{safeUrl}'"
            : $"-s {serial} shell am start -W -a android.intent.action.VIEW -d '{safeUrl}'";
        return true;
    }

    private static string EscapeSingleQuotedShell(string value)
    {
        return value.Replace("'", "'\\''");
    }

    public async Task<(bool Success, string Message)> PairAsync(string ipPort, string code)
    {
        var result = await RunAdbAsync($"pair {ipPort} {code}", 15000);
        if (result.Success && result.Output.Contains("successfully"))
            return (true, "Pairing successful.");
        return (false, string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output);
    }

    public async Task<(bool Success, string Message)> ConnectAsync(string ipPort)
    {
        var result = await RunAdbAsync($"connect {ipPort}", 10000);
        if (result.Success && result.Output.Contains("connected"))
            return (true, $"Connected to {ipPort}");
        return (false, string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output);
    }

    public async Task<(bool Success, string Message)> DisconnectAsync(string ipPort)
    {
        var result = await RunAdbAsync($"disconnect {ipPort}", 5000);
        return (result.Success, result.Output.Trim());
    }

    public async Task<List<string>> DiscoverPairingPortsAsync()
    {
        var ports = new List<string>();
        var testPorts = new[] { 47201, 47202, 47203 };

        foreach (var port in testPorts)
        {
            var result = await RunAdbAsync($"pair 127.0.0.1:{port}", 3000);
            if (result.Output.Contains("Listening"))
            {
                ports.Add($"127.0.0.1:{port}");
            }
        }

        return ports;
    }
}
