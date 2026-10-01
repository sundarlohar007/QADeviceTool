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

    public async Task<bool> BroadcastIntentAsync(string serial, string uri)
    {
        return (await LaunchDeepLinkAsync(serial, uri, new DeepLinkOptions()).ConfigureAwait(false)).Success;
    }

    public async Task<DeepLinkResult> LaunchDeepLinkAsync(string serial, string uri, DeepLinkOptions options, CancellationToken cancellationToken = default)
    {
        if (!DeepLinkHelper.TryValidate(uri, out var error)) return new(DeepLinkOutcome.Failed, error);
        if (!TryBuildDeepLinkArgs(serial, uri, options, false, out var args))
            return new(DeepLinkOutcome.Failed, "Invalid device selector, package, or conflicting intent target.");
        // Launches are never retried: the app may already have received the intent.
        var result = await ToolLauncher.RunAsync(_adb, args, 30000, cancellationToken: cancellationToken,
            hidePayloadInLogs: true, gateTimeoutMs: 15000).ConfigureAwait(false);
        return DeepLinkHelper.ParseResult(result);
    }

    public async Task<DeepLinkInspection> InspectDeepLinkAsync(string serial, string uri, DeepLinkOptions options, CancellationToken cancellationToken = default)
    {
        if (!DeepLinkHelper.TryValidate(uri, out var error)) return new(false, Array.Empty<string>(), error);
        if (!TryBuildDeepLinkArgs(serial, uri, options, true, out var args))
            return new(false, Array.Empty<string>(), "Invalid device selector, package, or conflicting intent target.");
        var result = await ToolLauncher.RunAsync(_adb, args, 10000, cancellationToken: cancellationToken,
            hidePayloadInLogs: true, gateTimeoutMs: 15000).ConfigureAwait(false);
        var text = result.Output + "\n" + result.Error;
        if (cancellationToken.IsCancellationRequested) return new(false, Array.Empty<string>(), "Inspection cancelled.");
        if (!result.Success || Regex.IsMatch(text, @"(?im)^\s*(?:Error:|Unknown command|Exception|java\.)"))
            return new(false, Array.Empty<string>(), "Handler inspection failed or is unsupported on this Android version. Check USB authorization and connectivity.");
        var handlers = Regex.Matches(result.Output, @"(?m)^\s*([A-Za-z0-9_.$]+/[A-Za-z0-9_.$]+)\s*$")
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(100).ToArray();
        if (handlers.Length == 0 && !text.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
            return new(false, handlers, "Android returned an unrecognized handler response; no handler count can be confirmed.");
        return new(true, handlers, handlers.Length switch
        {
            0 => "No installed activity handles this link.",
            1 => "One matching activity found. Android permissions may still prevent launching it.",
            _ => $"{handlers.Length} matching activities found. Choose a target package to narrow the test."
        });
    }

    public async Task<string> ExecuteCommandAsync(string serial, string command, CancellationToken cancellationToken = default)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || SecurityHelper.IsNetworkCapableCommand(command))
            return "Blocked by LogPro offline security policy.";
        var result = await RunAdbAsync($"-s {serial} {command}", DefaultTimeoutMs, cancellationToken: cancellationToken);
        return result.Success ? result.Output : result.Error;
    }


    private const int DefaultTimeoutMs = 8000;
    private const int FastTimeoutMs = 5000;
    private const int MaxRetryAttempts = 2;
    private const int RetryDelayMs = 500;
    private static readonly Regex DeviceSelectorArgument = new(
        @"(?:^|\s)-s\s+(?:""(?<serial>[^""]+)""|(?<serial>\S+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AdbVersionPattern = new(
        @"version ([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex LsListingPattern = new(
        @"^(?<perm>[bcdlps-][rwx-]{9})\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(?::\d{2})?)\s+(?<name>.+)$",
        RegexOptions.Compiled);
    private static readonly Regex SafePathPattern = new(
        @"^[\p{L}\p{N}._\-/ '()+@,\[\]]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public AdbService()
    {
        _adb = ToolResolver.Resolve("adb");
    }

    internal AdbService(string executablePath) => _adb = executablePath;

    // ─── Semaphore-guarded ADB execution ─────────────────────────
    // All adb calls go through these to prevent concurrent USB transport access.

    private async Task<ToolLauncherResult> RunAdbAsync(string arguments, int timeoutMs = DefaultTimeoutMs,
        Action<string>? outputCallback = null, CancellationToken cancellationToken = default, bool forwardErrorToCallback = false)
    {
        return await RunAdbWithRetryAsync(arguments, timeoutMs, outputCallback, cancellationToken, forwardErrorToCallback);
    }

    private async Task<ToolLauncherResult> RunAdbWithRetryAsync(string arguments, int timeoutMs,
        Action<string>? outputCallback, CancellationToken cancellationToken = default, bool forwardErrorToCallback = false)
    {
        try
        {
            var selector = DeviceSelectorArgument.Match(arguments);
            if (selector.Success && !SecurityHelper.IsValidOfflineDeviceSelector(selector.Groups["serial"].Value))
                return new ToolLauncherResult { Success = false, Error = "Blocked by LogPro offline security policy." };

            ToolLauncherResult? result = null;
            // A retry can replay an install, shell mutation, or file transfer. Keep it to
            // discovery/property reads and only on known transient transport errors.
            var readOnly = Regex.IsMatch(arguments,
                @"^(?:devices\b|version\b|-s\s+\S+\s+shell\s+(?:getprop\b|dumpsys\b|cat\s+/proc/))",
                RegexOptions.CultureInvariant);
            for (int retry = 0; retry < (readOnly ? MaxRetryAttempts : 1); retry++)
            {
                result = await ToolLauncher.RunAsync(_adb, arguments, timeoutMs, outputCallback, cancellationToken, forwardErrorToCallback).ConfigureAwait(false);
                if (result.Success) return result;

                var transient = result.Error.Contains("device offline", StringComparison.OrdinalIgnoreCase) ||
                    result.Error.Contains("device not found", StringComparison.OrdinalIgnoreCase) ||
                    result.Error.Contains("transport", StringComparison.OrdinalIgnoreCase) ||
                    result.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase);
                if (!transient) break;

                if (retry < MaxRetryAttempts - 1)
                    await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
            }

            return result!;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[AdbService] Exception in RunAdbAsync");
            return new ToolLauncherResult { Success = false, Error = ex.Message };
        }
    }

    private async Task<System.Diagnostics.Process?> StartAdbLongRunning(string arguments, bool drainStdout = true)
    {
        try
        {
            return ToolLauncher.StartLongRunning(_adb, arguments, drainStdout: drainStdout);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[AdbService] Failed to start long-running ADB process");
            return null;
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
            var match = AdbVersionPattern.Match(result.Output);
            status.Version = match.Success ? match.Groups[1].Value : "Installed";
            status.Path = ToolResolver.IsBundled(_adb) ? $"Bundled: {_adb}" : (PathHelper.FindInPath("adb") ?? "In PATH");
            status.StatusMessage = "ADB is ready";
        }
        else
        {
            AppLogger.Log.Warn($"[AdbService] CheckAvailabilityAsync failed. Error: {SecurityHelper.RedactSensitiveText(result.Error)}, Output: {SecurityHelper.RedactSensitiveText(result.Output)}");
            status.IsInstalled = false;
            status.StatusMessage = "ADB not found. Place platform-tools in the tools/ folder.";
        }

        return status;
    }

    public async Task<List<DeviceInfo>> GetConnectedDevicesAsync()
        => (await GetConnectedDevicesWithStatusAsync().ConfigureAwait(false)).Devices;

    public async Task<(bool Success, List<DeviceInfo> Devices)> GetConnectedDevicesWithStatusAsync()
    {
        var devices = new List<DeviceInfo>();

        try
        {
            var result = await RunAdbAsync("devices -l", DefaultTimeoutMs);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                AppLogger.Log.Debug("[AdbService] No devices found or ADB command failed");
                return (result.Success && !string.IsNullOrWhiteSpace(result.Output), devices);
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
                        device.Model = !string.IsNullOrEmpty(device.Name) ? device.Name :
                            !string.IsNullOrEmpty(device.Product) ? device.Product : serial;
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
            return (false, devices);
        }

        return (true, devices);
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
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || !SecurityHelper.IsSafeDeviceArgument(property)) return null;
        var result = await RunAdbAsync($"-s {serial} shell getprop {property}", FastTimeoutMs);
        return result.Success ? result.Output.Trim() : null;
    }

    public async Task<(bool Success, string Output, string Error)> ExecuteCommandWithResultAsync(string serial, string args,
        CancellationToken cancellationToken = default)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || SecurityHelper.IsNetworkCapableCommand(args))
            return (false, string.Empty, "Blocked by LogPro offline security policy.");
        var result = await RunAdbAsync($"-s {serial} {args}", DefaultTimeoutMs, cancellationToken: cancellationToken);
        return (result.Success, result.Output, result.Error);
    }


    public async Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device)
    {
        if (device.ConnectionState != DeviceConnectionState.Online)
        {
            AppLogger.Log.Debug($"[AdbService] Skipping details for {SecurityHelper.HashSerial(device.Serial)} - device is {device.ConnectionState}");
            return device;
        }

        try
        {
            var osTask = GetDevicePropertyAsync(device.Serial, "ro.build.version.release");
            var batteryTask = RunAdbAsync($"-s {device.Serial} shell dumpsys battery", FastTimeoutMs);
            var mfrTask = GetDevicePropertyAsync(device.Serial, "ro.product.manufacturer");
            var modelTask = GetDevicePropertyAsync(device.Serial, "ro.product.model");

            await Task.WhenAll(osTask, batteryTask, mfrTask, modelTask);

            device.OsVersion = osTask.Result ?? "Unknown";

            var batteryResult = batteryTask.Result;
            if (batteryResult.Success)
            {
                var match = Regex.Match(batteryResult.Output, @"level:\s*(\d+)");
                if (match.Success)
                    device.BatteryLevel = $"{match.Groups[1].Value}%";

                var matchStatus = Regex.Match(batteryResult.Output, @"status:\s*(\w+)");
                if (matchStatus.Success)
                    device.BatteryStatus = matchStatus.Groups[1].Value;
            }

            var manufacturer = mfrTask.Result;
            if (!string.IsNullOrEmpty(manufacturer))
                device.Manufacturer = manufacturer;
            if (!string.IsNullOrWhiteSpace(modelTask.Result))
                device.Model = modelTask.Result;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] Error getting details for {SecurityHelper.HashSerial(device.Serial)}");
        }

        return device;
    }

    public async Task<System.Diagnostics.Process?> StartLogCaptureAsync(string serial, string outputFilePath,
        LogcatBuffer buffer = LogcatBuffer.Main, LogcatFormat format = LogcatFormat.ThreadTime)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || !PathHelper.IsSafeLocalPath(outputFilePath))
            return null;

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

        // drainStdout: false — SessionService attaches its own OutputDataReceived + BeginOutputReadLine (BUG-01 fix-completion)
        return await StartAdbLongRunning($"-s {serial} logcat {bufferArg} -v {formatArg}", drainStdout: false).ConfigureAwait(false);
    }

    public async Task<bool> CaptureScreenshotAsync(string serial, string outputPath)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || !PathHelper.IsSafeLocalPath(outputPath))
        {
            AppLogger.Log.Warn("[AdbService] CaptureScreenshotAsync called with empty serial");
            return false;
        }

        try
        {
            var remotePath = $"/sdcard/qa_screenshot_{Guid.NewGuid():N}.png";
            try
            {
                var capResult = await RunAdbAsync($"-s {serial} shell screencap -p {remotePath}", DefaultTimeoutMs);
                if (!capResult.Success) return false;

                var pullResult = await RunAdbAsync($"-s {serial} pull {remotePath} \"{outputPath}\"", DefaultTimeoutMs);
                return pullResult.Success && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
            }
            finally
            {
                await RunAdbAsync($"-s {serial} shell rm {remotePath}", FastTimeoutMs);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] Screenshot failed for {SecurityHelper.HashSerial(serial)}");
            return false;
        }
    }

    /// <summary>
    /// Starts screen recording on device. Returns the remote path if started, null if failed.
    /// Recording runs until StopScreenRecord is called or max duration reached.
    /// </summary>
    public async Task<string?> StartScreenRecordAsync(string serial, string? outputDir = null, int maxDurationSec = 180, string bitRate = "8M")
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) ||
            (outputDir != null && !PathHelper.IsSafeLocalPath(outputDir)) ||
            maxDurationSec is < 1 or > 180 ||
            !Regex.IsMatch(bitRate, @"^\d+(?:\.\d+)?[KMG]$"))
            return null;

        await _screenRecordGate.WaitAsync().ConfigureAwait(false);

        try
        {
            // Prevent concurrent recordings on the same AdbService instance.
            if (_activeRecordProcess != null && !_activeRecordProcess.HasExited)
                return null;
            if (_activeRecordProcess != null)
            {
                _activeRecordProcess.Dispose();
                _activeRecordProcess = null;
            }

            var remotePath = $"/sdcard/qa_screenrecord_{Guid.NewGuid():N}.mp4";
            var arguments = $"-s {serial} shell screenrecord --bit-rate {bitRate} --time-limit {maxDurationSec} {remotePath}";
            var process = await StartAdbLongRunning(arguments);
            if (process == null) return null;

            _activeRecordProcess = process;
            _activeRecordRemotePath = remotePath;
            _activeRecordSerial = serial;
            _activeRecordOutputDir = outputDir;
            ProcessManager.Instance.TrackProcess(process);
            return remotePath;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] StartScreenRecord failed for {SecurityHelper.HashSerial(serial)}");
            return null;
        }
        finally { _screenRecordGate.Release(); }
    }

    /// <summary>
    /// Stops the active screen recording and pulls the video file to local storage.
    /// </summary>
    public async Task<string?> StopScreenRecordAsync(string serial, string? localOutputPath = null)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) ||
            (localOutputPath != null && !PathHelper.IsSafeLocalPath(localOutputPath)))
            return null;

        await _screenRecordGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!string.Equals(serial, _activeRecordSerial, StringComparison.Ordinal))
                return null;

            var process = Interlocked.Exchange(ref _activeRecordProcess, null);
            var remoteFile = Interlocked.Exchange(ref _activeRecordRemotePath, null);
            var outputDir = Interlocked.Exchange(ref _activeRecordOutputDir, null);
            _activeRecordSerial = null;

            if (process != null && !process.HasExited)
            {
                try
                {
                    var pidResult = await RunAdbAsync($"-s {serial} shell pidof screenrecord", FastTimeoutMs);
                    if (pidResult.Success && remoteFile != null)
                    {
                        foreach (var pid in pidResult.Output.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!int.TryParse(pid, out var parsedPid) || parsedPid <= 0) continue;
                            var cmdline = await RunAdbAsync($"-s {serial} shell cat /proc/{parsedPid}/cmdline", FastTimeoutMs);
                            if (cmdline.Success && cmdline.Output.Contains(remoteFile, StringComparison.Ordinal))
                                await RunAdbAsync($"-s {serial} shell kill -2 {parsedPid}", FastTimeoutMs);
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Log.Warn(ex, "[AdbService] StopScreenRecord failed"); }
                try
                {
                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(false); } catch (Exception ex) { AppLogger.Log.Debug(ex, "[AdbService] ScreenRecord kill error"); }
                }
            }
            process?.Dispose();

            if (string.IsNullOrEmpty(remoteFile))
                return null;
            var localPath = localOutputPath ?? Path.Combine(
                outputDir ?? Helpers.PathHelper.GetDefaultSessionsDirectory(),
                $"screenrecord_{SecurityHelper.HashSerial(serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            var pullResult = await RunAdbAsync($"-s {serial} pull \"{remoteFile}\" \"{localPath}\"", 30000);
            if (pullResult.Success)
                await RunAdbAsync($"-s {serial} shell rm \"{remoteFile}\"", FastTimeoutMs);

            return pullResult.Success ? localPath : null;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] StopScreenRecord failed for {SecurityHelper.HashSerial(serial)}");
            return null;
        }
        finally { _screenRecordGate.Release(); }
    }

    public bool IsScreenRecording { get { var p = _activeRecordProcess; return p != null && !p.HasExited; } }
    private System.Diagnostics.Process? _activeRecordProcess;
    private string? _activeRecordRemotePath;
    private string? _activeRecordSerial;
    private string? _activeRecordOutputDir;
    private readonly SemaphoreSlim _screenRecordGate = new(1, 1);

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
            AppLogger.Log.Error(ex, $"[AdbService] GetPidFromPackageNameAsync failed for {SecurityHelper.RedactSensitiveText(packageNameKeyword)}");
        }

        return null;
    }

    public Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? outputCallback = null)
        => InstallApkAsync(serial, apkPath, outputCallback, CancellationToken.None);

    public Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? outputCallback, CancellationToken cancellationToken)
        => InstallApkAsync(serial, apkPath, outputCallback, cancellationToken, false);

    public async Task<(bool Success, string Message)> InstallApkAsync(string serial, string apkPath, Action<string>? outputCallback, CancellationToken cancellationToken, bool allowTestApk)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || !PathHelper.IsSafeLocalPath(apkPath))
            return (false, "Invalid local path or device selector.");
        var result = await RunAdbAsync($"-s {serial} install -r {(allowTestApk ? "-t " : "")}{ToolLauncher.QuoteArgument(apkPath)}", 600000, outputCallback, cancellationToken, forwardErrorToCallback: true);

        if (result.Output.Contains("Failure", StringComparison.OrdinalIgnoreCase))
        {
            return (false, result.Output.Trim());
        }

        var lastLine = result.Output.Trim().Split('\n').LastOrDefault() ?? "";
        if (result.Success && lastLine.Trim().Equals("Success", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "APK installed successfully.");
        }

        return (false, !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : result.Error?.Trim() ?? "Install failed.");
    }

    public async Task<(bool Success, string Message)> EnableWirelessAsync(string serial, int port = 5555)
    {
        if (SecurityHelper.OfflineOnly) return (false, "Wireless ADB is disabled by the offline policy.");
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial))
            return (false, "Invalid device selector.");
        if (port < 1 || port > 65535)
            return (false, "Invalid port number.");
        var result = await RunAdbAsync($"-s {serial} tcpip {port}", FastTimeoutMs);
        return result.Success
            ? (true, $"Wireless ADB enabled on port {port}. Use 'adb connect <device-ip>:{port}' to connect.")
            : (false, result.Error);
    }

    public async Task<(bool Success, string Message)> ConnectWirelessAsync(string ipAddress, int port = 5555)
    {
        if (SecurityHelper.OfflineOnly) return (false, "Wireless ADB is disabled by the offline policy.");
        if (!SecurityHelper.IsPrivateSubnet(ipAddress))
            return (false, "Only private/lab network IPs are allowed (10.x, 172.16-31.x, 192.168.x). Public IPs are blocked.");
        var result = await RunAdbAsync($"connect {ipAddress}:{port}", FastTimeoutMs);
        return result.Success && result.Output.Contains("connected", StringComparison.OrdinalIgnoreCase)
            ? (true, $"Connected to {ipAddress}:{port}")
            : (false, string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    public async Task<(bool Success, string Message)> DisconnectWirelessAsync(string ipAddress, int port = 5555)
    {
        if (SecurityHelper.OfflineOnly) return (false, "Wireless ADB is disabled by the offline policy.");
        if (!SecurityHelper.IsPrivateSubnet(ipAddress))
            return (false, "Only private/lab network IPs are allowed.");
        var result = await RunAdbAsync($"disconnect {ipAddress}:{port}", FastTimeoutMs);
        return result.Success
            ? (true, $"Disconnected from {ipAddress}:{port}")
            : (false, result.Error);
    }

    public async Task<List<DeviceFile>> ListDirectoryAsync(string serial, string path)
    {
        try
        {
            if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || !IsSafePath(path))
                throw new ArgumentException("Unsupported Android device selector or path.", nameof(path));
            var safePath = path.Replace("'", "'\\''");
            var command = $"-s {serial} shell \"ls -lAL '{safePath}'\"";
            var result = await RunAdbAsync(command, DefaultTimeoutMs);
            if (result.Success)
            {
                var parsed = ParseAndroidLsListing(result.Output, path);
                if (parsed.Count > 0 || string.IsNullOrWhiteSpace(result.Output) ||
                    result.Output.Trim().StartsWith("total 0", StringComparison.OrdinalIgnoreCase))
                    return parsed;
            }

            // Detect permission denied on restricted paths like /data/
            var fallback = await RunAdbAsync($"-s {serial} shell \"ls -1Ap '{safePath}'\"", DefaultTimeoutMs);
            if (!fallback.Success)
                throw new IOException("ADB could not list this directory. It may be restricted on this Android device.");
            return ParseSimpleDirectoryListing(fallback.Output, path);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[AdbService] ListDirectoryAsync failed");
            throw;
        }
    }

    internal static List<DeviceFile> ParseAndroidLsListing(string output, string parentPath)
    {
        var files = new List<DeviceFile>();
        var basePath = NormalizeDevicePath(parentPath);
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("total ")) continue;

            var match = LsListingPattern.Match(line);

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
            var name = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") continue;

            var isDir = name.EndsWith("/");
            name = name.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(name)) continue;

            files.Add(new DeviceFile
            {
                Name = name,
                Path = CombineDevicePath(basePath, name),
                IsDirectory = isDir,
                Size = -1,
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
        => await PullFileAsync(serial, remotePath, localDestination, CancellationToken.None);

    public async Task<bool> PullFileAsync(string serial, string remotePath, string localDestination, CancellationToken cancellationToken)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || !IsSafePath(remotePath) || !PathHelper.IsSafeLocalPath(localDestination)) return false;
        var isDirectory = Directory.Exists(localDestination);
        var target = isDirectory ? localDestination : localDestination + ".logpro-part-" + Guid.NewGuid().ToString("N");
        try
        {
            var result = await RunAdbAsync($"-s {serial} pull {ToolLauncher.QuoteArgument(remotePath)} {ToolLauncher.QuoteArgument(target)}", 300000, cancellationToken: cancellationToken);
            if (!result.Success) return false;
            if (isDirectory) return Directory.Exists(Path.Combine(localDestination, Path.GetFileName(remotePath.TrimEnd('/'))));
            if (!File.Exists(target)) return false;
            File.Move(target, localDestination, true);
            return true;
        }
        finally { if (!isDirectory && File.Exists(target)) File.Delete(target); }
    }

    public async Task<bool> PushFileAsync(string serial, string localPath, string remoteDestination)
        => await PushFileAsync(serial, localPath, remoteDestination, CancellationToken.None);

    public async Task<bool> PushFileAsync(string serial, string localPath, string remoteDestination, CancellationToken cancellationToken)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || !IsSafePath(remoteDestination) || !PathHelper.IsSafeLocalPath(localPath)) return false;
        var result = await RunAdbAsync($"-s {serial} push {ToolLauncher.QuoteArgument(localPath)} {ToolLauncher.QuoteArgument(remoteDestination)}", 300000, cancellationToken: cancellationToken);
        return result.Success;
    }

    public async Task<bool> DeleteFileAsync(string serial, string remotePath)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || !IsAllowedFileDeletionPath(remotePath)) return false;
        var safePath = remotePath.Replace("'", "'\\''");
        var result = await RunAdbAsync($"-s {serial} shell \"rm -rf '{safePath}'\"");
        return result.Success;
    }

    private static bool IsSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!path.StartsWith('/') || path.Split('/').Any(segment => segment is "." or "..")) return false;

        return SafePathPattern.IsMatch(path);
    }

    internal static bool IsAllowedFileDeletionPath(string path)
    {
        if (!IsSafePath(path)) return false;
        var normalized = path.TrimEnd('/');
        if (normalized.Length == 0) return false;
        return normalized.StartsWith("/sdcard/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/storage/emulated/0/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/storage/self/primary/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/data/local/tmp/", StringComparison.OrdinalIgnoreCase);
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
            AppLogger.Log.Error(ex, $"[AdbService] ListInstalledAppsAsync failed for {SecurityHelper.HashSerial(serial)}");
        }

        return apps.OrderBy(a => a.Name).ToList();
    }

    public async Task<AppInventoryResult> GetAppInventoryAsync(string serial)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial))
            return AppInventoryResult.Failed("Invalid device selector.");

        try
        {
            var user = await RunAdbAsync($"-s {serial} shell pm list packages -3 --show-versioncode", 15000).ConfigureAwait(false);
            if (!user.Success)
                user = await RunAdbAsync($"-s {serial} shell pm list packages -3", 15000).ConfigureAwait(false);
            if (!user.Success) return AppInventoryResult.Failed(GetInventoryFailure(user));
            var system = await RunAdbAsync($"-s {serial} shell pm list packages -s --show-versioncode", 15000).ConfigureAwait(false);
            if (!system.Success)
                system = await RunAdbAsync($"-s {serial} shell pm list packages -s", 15000).ConfigureAwait(false);
            if (!system.Success) return AppInventoryResult.Failed(GetInventoryFailure(system));

            var apps = ParsePackageList(user.Output, AppCategory.User)
                .Concat(ParsePackageList(system.Output, AppCategory.System))
                .GroupBy(a => a.PackageId, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // One process-list command supplies running state for every package.
            var processes = await RunAdbAsync($"-s {serial} shell ps -A -o NAME", 10000).ConfigureAwait(false);
            if (processes.Success)
            {
                var names = processes.Output.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
                foreach (var app in apps)
                    app.IsRunning = names.Contains(app.PackageId) || names.Any(n => n.StartsWith(app.PackageId + ":", StringComparison.Ordinal));
            }
            return new AppInventoryResult(true, apps, RunningStateAvailable: processes.Success);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[AdbService] GetAppInventoryAsync failed for {SecurityHelper.HashSerial(serial)}");
            return AppInventoryResult.Failed(ex.Message);
        }
    }

    internal static List<AppItem> ParsePackageList(string output, AppCategory category)
        => output.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("package:", StringComparison.Ordinal))
            .Select(line => line["package:".Length..].Trim())
            .Select(line => new { Text = line, VersionAt = line.IndexOf(" versionCode:", StringComparison.Ordinal) })
            .Select(line => new
            {
                Package = line.VersionAt < 0 ? line.Text : line.Text[..line.VersionAt],
                Version = line.VersionAt < 0 ? "" : "code " + line.Text[(line.VersionAt + " versionCode:".Length)..].Trim()
            })
            .Where(line => SecurityHelper.IsValidPackageName(line.Package))
            .Select(line => new AppItem
            {
                PackageId = line.Package,
                Name = line.Package,
                Version = line.Version,
                Category = category,
                Platform = DevicePlatform.Android
            })
            .ToList();

    private static string GetInventoryFailure(ToolLauncherResult result)
        => SecurityHelper.RedactSensitiveText(!string.IsNullOrWhiteSpace(result.Error) ? result.Error :
            !string.IsNullOrWhiteSpace(result.Output) ? result.Output : $"ADB exited with code {result.ExitCode}.");

    public async Task<bool> UninstallAppAsync(string serial, string packageId)
    {
        if (!IsValidPackageName(packageId)) return false;
        var result = await RunAdbAsync($"-s {serial} uninstall {packageId}", DefaultTimeoutMs);
        return result.Success && result.Output.Contains("Success");
    }

    private static bool IsValidPackageName(string packageId)
        => SecurityHelper.IsValidPackageName(packageId);

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

    public Task<bool> SetDeviceClipboardAsync(string serial, string text)
    {
        // There is no portable ADB clipboard set command across supported Android builds.
        // A device-side companion app would be required to provide this feature reliably.
        return Task.FromResult(false);
    }

    public Task<string> GetDeviceClipboardAsync(string serial)
    {
        return Task.FromResult("Device clipboard reading is unavailable through portable ADB. Android clipboard privacy restrictions may also apply.");
    }

    public async Task<bool> SendNotificationAsync(string serial, string title, string body, string? channel = null)
    {
        if (!TryBuildNotificationArgs(serial, title, body, channel, out var args)) return false;
        var result = await RunAdbAsync(args, FastTimeoutMs);
        return result.Success;
    }

    internal static bool TryBuildNotificationArgs(string serial, string title, string body, string? channel, out string args)
    {
        args = string.Empty;
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || title.Length > 4096 || body.Length > 1_000_000 ||
            title.Contains('\n') || title.Contains('\r') || body.Contains('\n') || body.Contains('\r') ||
            (channel != null && !channel.Equals("default", StringComparison.OrdinalIgnoreCase)))
            return false;
        var tag = $"LogPro_{DateTime.Now.Ticks}";
        var safeTitle = EscapeSingleQuotedShell(title);
        var safeBody = EscapeSingleQuotedShell(body);
        args = $"-s {serial} shell cmd notification post -t '{safeTitle}' {tag} '{safeBody}'";
        return true;
    }

    internal static bool TryBuildDeepLinkIntentArgs(string serial, string url, out string args)
        => TryBuildDeepLinkArgs(serial, url, new DeepLinkOptions(), false, out args);

    internal static bool TryBuildDeepLinkArgs(string serial, string url, DeepLinkOptions options, bool inspect, out string args)
    {
        args = string.Empty;
        if (!SecurityHelper.IsValidOfflineDeviceSelector(serial) || !DeepLinkHelper.TryValidate(url, out _) ||
            !DeepLinkHelper.TryValidateOptions(url.Trim(), options, out _)) return false;
        var trimmed = url.Trim();
        var isIntentUri = trimmed.StartsWith("intent:", StringComparison.OrdinalIgnoreCase);
        if (isIntentUri) trimmed = "intent:" + trimmed[7..];
        if (isIntentUri && !string.IsNullOrEmpty(options.PackageId))
        {
            var package = Regex.Match(trimmed, @";package=([^;]+)").Groups[1].Value;
            // Insert a target into the intent itself so positional parsing cannot override it.
            if (package.Length == 0) trimmed = trimmed[..^3] + "package=" + options.PackageId + ";end";
        }
        var safeUrl = EscapeSingleQuotedShell(trimmed);
        var intentArgs = isIntentUri ? $"'{safeUrl}'" : $"-a android.intent.action.VIEW -d '{safeUrl}'";
        var extras = options.Browsable ? "-c android.intent.category.BROWSABLE " : string.Empty;
        if (!isIntentUri && options.PackageId.Length > 0) extras += "-p " + options.PackageId + " ";
        var command = inspect ? "cmd package query-activities --brief --components --user current " : "am start -W --user current ";
        // Quote for the host process AND the device shell. ADB receives one complete shell command.
        args = $"-s {serial} shell {ToolLauncher.QuoteArgument(command + extras + intentArgs)}";
        return true;
    }

    private static string EscapeSingleQuotedShell(string value)
    {
        return value.Replace("'", "'\\''");
    }

    public Task<(bool Success, string Message)> PairAsync(string ipPort, string code)
        => Task.FromResult((false, "Blocked by LogPro offline security policy: wireless ADB is disabled."));

    public Task<(bool Success, string Message)> ConnectAsync(string ipPort)
        => Task.FromResult((false, "Blocked by LogPro offline security policy: wireless ADB is disabled."));

    public Task<(bool Success, string Message)> DisconnectAsync(string ipPort)
        => Task.FromResult((false, "Blocked by LogPro offline security policy: wireless ADB is disabled."));

    public Task<List<string>> DiscoverPairingPortsAsync()
    {
        // BUG-10: hardcoded port scanning is broken (pairing ports are on the device, not localhost)
        // and fires bogus pairing attempts. Manual entry is supported — return empty.
        return Task.FromResult(new List<string>());
    }
}
