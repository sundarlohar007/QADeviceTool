using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Wraps pymobiledevice3 for all iOS operations — device detection, log capture,
/// screenshots, app management, and file access.
///
/// Resolution order for the pymobiledevice3 invoker:
///   1) bundled tools/pymobiledevice3/pymobiledevice3.exe (PyInstaller standalone)
///   2) system python.exe with `-m pymobiledevice3`
/// CheckAvailabilityAsync probes both and reports which one is active.
/// </summary>
public class IosService : IIosService
{
    private sealed record ToolSelection(string Exe, bool IsModuleInvocation, string ToolKind, ToolLauncherResult ProbeResult);
    private static readonly object SelectionLock = new();
    private static Task<ToolSelection>? _selection;
    private static DateTime _selectionAttempt;

    internal static void ResetToolSelection()
    {
        lock (SelectionLock) _selection = null;
    }

    private static Task<ToolSelection> GetToolAsync()
    {
        lock (SelectionLock)
        {
            if (_selection == null || (_selection.IsCompleted &&
                (!_selection.IsCompletedSuccessfully || !_selection.Result.ProbeResult.Success) &&
                DateTime.UtcNow - _selectionAttempt > TimeSpan.FromSeconds(30)))
            {
                _selectionAttempt = DateTime.UtcNow;
                _selection = SelectToolAsync();
            }
            return _selection;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Checked, DeviceInfo Device)> _readiness = new();
    private const int DefaultTimeoutMs = 45000;
    private const int InfoTimeoutMs = 45000;
    private const int InstallTimeoutMs = 600000;
    private const int CliProbeTimeoutMs = 45000;

    private static async Task<ToolSelection> SelectToolAsync()
    {
        if (!ToolResolver.BundledToolsTrusted)
            return new ToolSelection("", false, "blocked", new ToolLauncherResult
            { Error = "Bundled iOS tool failed verification. Repair LogPro using a verified installer." });
        var bundled = ResolveBundledExe();
        var systemPython = ResolveSystemPython();
        ToolLauncherResult? bundledProbe = null;
        if (bundled != null)
        {
            bundledProbe = await ToolLauncher.RunAsync(bundled, "--no-color syslog live --help", CliProbeTimeoutMs).ConfigureAwait(false);
            if (bundledProbe.Success)
                return new ToolSelection(bundled, false, $"bundled ({bundled})", bundledProbe);
        }
        if (systemPython != null)
        {
            var pythonProbe = await ToolLauncher.RunAsync(systemPython, "-m pymobiledevice3 --no-color syslog live --help", CliProbeTimeoutMs).ConfigureAwait(false);
            if (pythonProbe.Success)
                return new ToolSelection(systemPython, true, $"python -m pymobiledevice3 ({systemPython})", pythonProbe);
            if (bundledProbe == null)
                return new ToolSelection(systemPython, true, $"python -m pymobiledevice3 ({systemPython})", pythonProbe);
        }

        // Retain the bundled path so the dependency check can report its real import error.
        var exe = bundled ?? systemPython ?? "python";
        var probe = bundledProbe ?? new ToolLauncherResult { Error = "pymobiledevice3 is not installed." };
        return new ToolSelection(exe, bundled == null, bundled != null ? $"bundled ({bundled})" : $"python -m pymobiledevice3 ({exe})", probe);
    }

    private static string? ResolveBundledExe()
    {
        var path = Path.Combine(ToolLauncher.ToolsDirectory, "pymobiledevice3", "pymobiledevice3.exe");
        return File.Exists(path) ? path : null;
    }

    private static string? ResolveSystemPython()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathVar.Split(Path.PathSeparator)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.Combine(p, "python.exe"))
            .FirstOrDefault(File.Exists);
    }

    internal static string BuildCommandArgs(bool isModuleInvocation, string? udid, string subcommand)
    {
        var udidFlag = string.IsNullOrEmpty(udid) ? "" : $" --udid {Quote(udid)}";
        var prefix = isModuleInvocation ? "-m pymobiledevice3 " : "";
        return $"{prefix}--no-color {subcommand}{udidFlag}";
    }

    private static string Quote(string s) => ToolLauncher.QuoteArgument(s);

    private async Task<ToolLauncherResult> RunAsync(string? udid, string subcommand, int timeoutMs = DefaultTimeoutMs,
        Action<string>? outputCallback = null, CancellationToken cancellationToken = default, bool forwardErrorToCallback = false)
    {
        if (udid != null && !SecurityHelper.IsValidOfflineDeviceSelector(udid))
            return new ToolLauncherResult { Success = false, Error = "Blocked by LogPro offline security policy." };
        if (string.IsNullOrWhiteSpace(subcommand) || subcommand.Any(c => c is '\r' or '\n'))
            return new ToolLauncherResult { Success = false, Error = "Invalid iOS command." };
        var tool = await GetToolAsync().ConfigureAwait(false);
        if (!tool.ProbeResult.Success) return tool.ProbeResult;
        return await ToolLauncher.RunAsync(tool.Exe, BuildCommandArgs(tool.IsModuleInvocation, udid, subcommand), timeoutMs, outputCallback, cancellationToken, forwardErrorToCallback).ConfigureAwait(false);
    }

    private static string GetFailureMessage(ToolLauncherResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.Error) ? result.Error : result.Output;
        var line = text.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        return SecurityHelper.RedactSensitiveText(string.IsNullOrWhiteSpace(line) ? $"exit code {result.ExitCode}" : line);
    }

    private async Task<System.Diagnostics.Process?> StartLongAsync(string? udid, string subcommand, bool drainStdout = true)
    {
        var tool = await GetToolAsync().ConfigureAwait(false);
        if (!tool.ProbeResult.Success) throw new InvalidOperationException(tool.ProbeResult.Error);
        return ToolLauncher.StartLongRunning(tool.Exe, BuildCommandArgs(tool.IsModuleInvocation, udid, subcommand), drainStdout: drainStdout);
    }

    public Task<ToolLauncherResult> ExecuteCommandAsync(string? udid, string subcommand, int timeoutMs = DefaultTimeoutMs,
        Action<string>? outputCallback = null, CancellationToken cancellationToken = default)
        => RunAsync(udid, subcommand, timeoutMs, outputCallback, cancellationToken);

    public async Task<ToolStatus> CheckAvailabilityAsync()
    {
        try
        {
            var tool = await GetToolAsync().ConfigureAwait(false);
            var version = tool.ProbeResult.Success ? await RunAsync(null, "version", CliProbeTimeoutMs).ConfigureAwait(false) : tool.ProbeResult;
            var statusMsg = tool.ProbeResult.Success && version.Success
                ? $"Ready — {tool.ToolKind}"
                : $"Failed: {GetFailureMessage(tool.ProbeResult.Success ? version : tool.ProbeResult)}";
            return new ToolStatus
            {
                Name = "pymobiledevice3 (iOS Tools)",
                Description = "Required for iOS device communication",
                IsInstalled = tool.ProbeResult.Success && version.Success,
                Version = version.Success ? version.Output.Trim() : "n/a",
                Path = tool.Exe,
                StatusMessage = statusMsg
            };
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[IosService] CheckAvailabilityAsync failed");
            return new ToolStatus { Name = "pymobiledevice3 (iOS Tools)", IsInstalled = false, StatusMessage = ex.Message };
        }
    }

    public async Task<List<DeviceInfo>> GetConnectedDevicesAsync()
        => (await GetConnectedDevicesWithStatusAsync().ConfigureAwait(false)).Devices;

    public async Task<(bool Success, List<DeviceInfo> Devices)> GetConnectedDevicesWithStatusAsync()
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var result = await RunAsync(null, "usbmux list", InfoTimeoutMs).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return (false, devices);

            var output = result.Output.TrimStart();
            if (!output.StartsWith("[", StringComparison.Ordinal)) return (false, devices);

            using var json = JsonDocument.Parse(output);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return (false, devices);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var udid = item.TryGetProperty("UniqueDeviceID", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(udid)) continue;
                var name = item.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? "iOS Device" : "iOS Device";
                var model = item.TryGetProperty("ProductType", out var pt) ? pt.GetString() ?? "" : "";
                var osVer = item.TryGetProperty("ProductVersion", out var pv) ? pv.GetString() ?? "" : "";
                var connType = item.TryGetProperty("ConnectionType", out var ct) ? ct.GetString() ?? "USB" : "USB";

                devices.Add(new DeviceInfo
                {
                    Serial = udid,
                    Id = udid,
                    Name = name,
                    Model = model,
                    OsVersion = osVer,
                    Platform = DevicePlatform.iOS,
                    ConnectionState = connType.Equals("Unavailable", StringComparison.OrdinalIgnoreCase)
                        ? DeviceConnectionState.Offline
                        : DeviceConnectionState.Online
                });
            }
        }
        catch (JsonException ex)
        {
            AppLogger.Log.Warn(ex, "[IosService] Failed to parse usbmux JSON output");
            return (false, devices);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[IosService] GetConnectedDevicesAsync failed");
            return (false, devices);
        }
        var present = devices.Select(d => d.Serial).ToHashSet();
        foreach (var serial in _readiness.Keys.Where(k => !present.Contains(k))) _readiness.TryRemove(serial, out _);
        await Task.WhenAll(devices.Select(async device =>
        {
            if (_readiness.TryGetValue(device.Serial, out var cached) &&
                DateTime.UtcNow - cached.Checked < TimeSpan.FromSeconds(cached.Device.ConnectionState == DeviceConnectionState.Online ? 60 : 10))
            {
                device.ConnectionState = cached.Device.ConnectionState;
                device.Name = cached.Device.Name;
                device.OsVersion = cached.Device.OsVersion;
                return;
            }
            await GetDeviceDetailsAsync(device).ConfigureAwait(false);
            _readiness[device.Serial] = (DateTime.UtcNow, device.WithTemporaryUnavailable(false));
        })).ConfigureAwait(false);
        return (true, devices);
    }

    public async Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device)
    {
        try
        {
            var result = await RunAsync(device.Serial, "lockdown info", InfoTimeoutMs).ConfigureAwait(false);
            if (!result.Success)
            {
                device.ConnectionState = DeviceConnectionState.Offline;
                if ((result.Error ?? "").Contains("trust", StringComparison.OrdinalIgnoreCase) ||
                    (result.Error ?? "").Contains("paired", StringComparison.OrdinalIgnoreCase))
                    device.ConnectionState = DeviceConnectionState.PendingTrust;
                return device;
            }

            device.ConnectionState = DeviceConnectionState.Online;
            ParseLockdownInfo(result.Output ?? "", device);
            if (string.IsNullOrEmpty(device.Name)) device.Name = device.Model ?? "iOS Device";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, $"[IosService] GetDeviceDetailsAsync failed for {SecurityHelper.HashSerial(device.Serial)}"); }
        return device;
    }

    /// <summary>
    /// Parses lockdown info output. pymobiledevice3 emits a Python-dict-like structure:
    ///   {'DeviceName': 'iPhone', 'ProductType': 'iPhone14,2', ...}
    /// or JSON with --no-color. Supports both via regex extraction.
    /// </summary>
    internal static void ParseLockdownInfo(string output, DeviceInfo device)
    {
        if (string.IsNullOrWhiteSpace(output)) return;

        var trimmed = output.TrimStart();
        if (trimmed.StartsWith("{"))
        {
            // Try strict JSON first
            try
            {
                using var json = JsonDocument.Parse(trimmed);
                var root = json.RootElement;
                if (root.TryGetProperty("DeviceName", out var dn)) device.Name = dn.GetString() ?? device.Name;
                if (root.TryGetProperty("ProductType", out var pt)) device.Model = pt.GetString() ?? device.Model;
                if (root.TryGetProperty("ProductVersion", out var pv)) device.OsVersion = pv.GetString() ?? device.OsVersion;
                if (root.TryGetProperty("BatteryCurrentCapacity", out var bc)) device.BatteryLevel = bc.ToString() + "%";
                return;
            }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[IosService] parse fallback to regex"); }
        }

        // Fallback: regex scan. Values can be single/double-quoted (any char) or bare
        // (no comma/brace/newline). Quoted form preserves embedded commas like 'iPhone15,3'.
        var rx = new System.Text.RegularExpressions.Regex(
            @"['""]?(?<key>[A-Za-z][A-Za-z0-9]+)['""]?\s*[:=]\s*(?:'(?<v1>[^']*)'|""(?<v2>[^""]*)""|(?<v3>[^\r\n}]+))");
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(output))
        {
            var key = m.Groups["key"].Value;
            var val = (m.Groups["v1"].Success ? m.Groups["v1"].Value
                     : m.Groups["v2"].Success ? m.Groups["v2"].Value
                     : m.Groups["v3"].Value).Trim();
            switch (key)
            {
                case "DeviceName": device.Name = val; break;
                case "ProductType": device.Model = val; break;
                case "ProductVersion": device.OsVersion = val; break;
                case "BatteryCurrentCapacity": device.BatteryLevel = val + "%"; break;
            }
        }
    }

    public async Task<System.Diagnostics.Process?> StartLogCaptureAsync(string udid, string outputFilePath)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !PathHelper.IsSafeLocalPath(outputFilePath))
            return null;
        try
        {
            var ready = await RunAsync(udid, "lockdown info", InfoTimeoutMs).ConfigureAwait(false);
            if (!ready.Success) throw new InvalidOperationException($"iOS is not ready for logging: {GetFailureMessage(ready)}. Unlock the device and accept Trust This Computer.");
            // syslog live streams to stdout by default; SessionService reads stdout and
            // writes the file itself. Using --out would bypass the capture pipeline entirely.
            return await StartLongAsync(udid, "syslog live", drainStdout: false).ConfigureAwait(false);
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] StartLogCapture failed"); throw; }
    }

    public async Task<bool> CaptureScreenshotAsync(string udid, string outputPath)
        => (await CaptureScreenshotWithStatusAsync(udid, outputPath).ConfigureAwait(false)).Success;

    public async Task<(bool Success, string Message)> CaptureScreenshotWithStatusAsync(string udid, string outputPath)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !PathHelper.IsSafeLocalPath(outputPath))
            return (false, "Invalid device or output path.");
        try
        {
            // developer screenshot uses the deprecated lockdown screenshot service — works without DeveloperDiskImage.
            var result = await RunAsync(udid, $"developer screenshot {Quote(outputPath)}", DefaultTimeoutMs).ConfigureAwait(false);
            if (result.Success && File.Exists(outputPath)) return (true, "Screenshot saved.");
            var error = GetFailureMessage(result);
            if (error.Contains("Developer", StringComparison.OrdinalIgnoreCase))
                return (false, "iOS screenshot requires Developer Mode and possibly a mounted Developer Disk Image.");
            return (false, result.Success ? "Screenshot command succeeded but produced no image." : $"iOS screenshot failed: {error}");
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] CaptureScreenshotAsync failed"); return (false, ex.Message); }
    }

    public Task<(bool Success, string Message)> InstallIpaAsync(string udid, string ipaPath, Action<string>? outputCallback = null)
        => InstallIpaAsync(udid, ipaPath, outputCallback, CancellationToken.None);

    public async Task<(bool Success, string Message)> InstallIpaAsync(string udid, string ipaPath, Action<string>? outputCallback, CancellationToken cancellationToken)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !PathHelper.IsSafeLocalPath(ipaPath))
            return (false, "Invalid local path or device selector.");
        try
        {
            outputCallback?.Invoke($"Installing: {ipaPath}");
            var result = await RunAsync(udid, $"apps install {Quote(ipaPath)}", InstallTimeoutMs, outputCallback, cancellationToken, forwardErrorToCallback: true).ConfigureAwait(false);
            if (result.Success) return (true, "IPA installed successfully.");
            var error = result.Error ?? result.Output ?? $"Exit code: {result.ExitCode}";
            return (false, $"Install failed: {error.Trim()}");
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] InstallIpaAsync failed"); return (false, ex.Message); }
    }

    public async Task<List<AppItem>> ListInstalledAppsAsync(string udid)
    {
        var apps = new List<AppItem>();
        try
        {
            var result = await RunAsync(udid, "apps list", DefaultTimeoutMs).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return apps;
            apps = ParseAppsList(result.Output);
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] ListInstalledAppsAsync failed"); }
        return apps.OrderBy(a => a.Name).ToList();
    }

    public async Task<AppInventoryResult> GetAppInventoryAsync(string udid)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid))
            return AppInventoryResult.Failed("Invalid device selector.");
        try
        {
            var apps = new List<AppItem>();
            foreach (var (type, category) in new[] {
                ("User", AppCategory.User), ("System", AppCategory.System), ("Hidden", AppCategory.Hidden) })
            {
                var result = await RunAsync(udid, $"apps list --type {type}", DefaultTimeoutMs).ConfigureAwait(false);
                if (!result.Success)
                    return AppInventoryResult.Failed(GetFailureMessage(result));
                if (string.IsNullOrWhiteSpace(result.Output)) continue;
                var parsed = ParseAppsList(result.Output, category);
                if (parsed.Count == 0 && result.Output.Trim() is not "{}")
                    return AppInventoryResult.Failed($"Could not parse {type.ToLowerInvariant()} application list.");
                apps.AddRange(parsed);
            }
            return new AppInventoryResult(true, apps.GroupBy(a => a.PackageId, StringComparer.Ordinal)
                .Select(g => g.First()).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[IosService] GetAppInventoryAsync failed");
            return AppInventoryResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Parses `apps list` output. pymobiledevice3 emits a top-level dict keyed by bundle id.
    /// </summary>
    internal static List<AppItem> ParseAppsList(string output, AppCategory category = AppCategory.Unknown)
    {
        var apps = new List<AppItem>();
        var trimmed = output.TrimStart();

        if (trimmed.StartsWith("{"))
        {
            try
            {
                using var json = JsonDocument.Parse(trimmed);
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in json.RootElement.EnumerateObject())
                    {
                        var pkg = prop.Name;
                        var info = prop.Value;
                        if (info.ValueKind != JsonValueKind.Object) continue;
                        var name = info.TryGetProperty("CFBundleDisplayName", out var dn) ? dn.GetString() ?? pkg
                                 : info.TryGetProperty("CFBundleName", out var bn) ? bn.GetString() ?? pkg
                                 : pkg;
                        var ver = info.TryGetProperty("CFBundleShortVersionString", out var vs) ? vs.GetString() ?? ""
                                : info.TryGetProperty("CFBundleVersion", out var bv) ? bv.GetString() ?? "" : "";
                        apps.Add(new AppItem { PackageId = pkg, Name = name, Version = ver, Platform = DevicePlatform.iOS, Category = category });
                    }
                    return apps;
                }
            }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[IosService] parse fallback to text"); }
        }

        // Text fallback: lines like "com.foo.bar:" with indented version/name beneath.
        string? currentPkg = null;
        string? currentName = null;
        string? currentVer = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!char.IsWhiteSpace(line[0]) && line.Contains('.') && line.TrimEnd().EndsWith(":"))
            {
                if (currentPkg != null)
                    apps.Add(new AppItem { PackageId = currentPkg, Name = currentName ?? currentPkg, Version = currentVer ?? "", Platform = DevicePlatform.iOS, Category = category });
                currentPkg = line.TrimEnd(':', ' ');
                currentName = null;
                currentVer = null;
                continue;
            }

            var trimmedLine = line.Trim();
            if (trimmedLine.StartsWith("CFBundleDisplayName", StringComparison.OrdinalIgnoreCase))
                currentName = ExtractValue(trimmedLine);
            else if (trimmedLine.StartsWith("CFBundleShortVersionString", StringComparison.OrdinalIgnoreCase))
                currentVer = ExtractValue(trimmedLine);
        }
        if (currentPkg != null)
            apps.Add(new AppItem { PackageId = currentPkg, Name = currentName ?? currentPkg, Version = currentVer ?? "", Platform = DevicePlatform.iOS, Category = category });
        return apps;
    }

    private static string ExtractValue(string keyValueLine)
    {
        var idx = keyValueLine.IndexOf(':');
        if (idx < 0) return "";
        return keyValueLine.Substring(idx + 1).Trim().Trim('\'', '"', ',');
    }

    public async Task<bool> UninstallAppAsync(string udid, string packageId)
    {
        if (!SecurityHelper.IsValidBundleId(packageId)) return false;
        try
        {
            var result = await RunAsync(udid, $"apps uninstall {Quote(packageId)}", DefaultTimeoutMs).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] UninstallAppAsync failed"); return false; }
    }

    public async Task<List<DeviceFile>> ListDirectoryAsync(string udid, string path)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !IsSafePath(path)) return new List<DeviceFile>();
        var files = new List<DeviceFile>();
        try
        {
            var result = await RunAsync(udid, $"afc ls {Quote(path)}", DefaultTimeoutMs).ConfigureAwait(false);
            if (!result.Success)
                throw new IOException($"iOS AFC could not list {path}: {GetFailureMessage(result)}");
            if (string.IsNullOrWhiteSpace(result.Output)) return files;
            files = ParseAfcLs(result.Output, path);
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] ListDirectoryAsync failed"); throw; }
        return files;
    }

    /// <summary>
    /// Parses `afc ls` output. pymobiledevice3 emits one entry per line (just names),
    /// optionally with a trailing slash for directories.
    /// </summary>
    internal static List<DeviceFile> ParseAfcLs(string output, string parentPath)
    {
        var files = new List<DeviceFile>();
        var basePath = NormalizeDevicePath(parentPath);
        foreach (var line in output.Split('\n', '\r'))
        {
            var rawName = line.TrimEnd('\r');
            if (string.IsNullOrEmpty(rawName)) continue;
            // Skip noise (dot entries, total lines)
            if (rawName == "." || rawName == "..") continue;
            // pymobiledevice3 9.12.0 dirlist prints the requested directory first.
            if (NormalizeDevicePath(rawName.TrimEnd('/')) == basePath) continue;

            var hadTrailingSlash = rawName.EndsWith("/");
            var normalized = rawName.Trim('/');
            var name = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalized;
            if (string.IsNullOrWhiteSpace(name)) continue;

            // A trailing slash is the only directory marker available in plain ls output.
            // Callers should request a directory-aware format when supported by the CLI.
            var isDir = hadTrailingSlash;

            files.Add(new DeviceFile
            {
                Name = name,
                Path = CombineDevicePath(basePath, name),
                IsDirectory = isDir,
                Size = -1,
                ModifiedDate = DateTime.MinValue
            });
        }
        return files;
    }

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

    public async Task<bool> PullFileAsync(string udid, string remotePath, string localPath)
        => await PullFileAsync(udid, remotePath, localPath, CancellationToken.None);

    public async Task<bool> PullFileAsync(string udid, string remotePath, string localPath, CancellationToken cancellationToken)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !IsSafePath(remotePath) || !PathHelper.IsSafeLocalPath(localPath))
        { AppLogger.Log.Warn("[IosService] Unsafe path rejected"); return false; }
        try
        {
            var isDirectory = Directory.Exists(localPath);
            var target = isDirectory ? localPath : localPath + ".logpro-part-" + Guid.NewGuid().ToString("N");
            try
            {
                var result = await RunAsync(udid, $"afc pull {Quote(remotePath)} {Quote(target)}", 300000, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!result.Success) return false;
                if (isDirectory) return Directory.Exists(Path.Combine(localPath, Path.GetFileName(remotePath.TrimEnd('/'))));
                if (!File.Exists(target)) return false;
                File.Move(target, localPath, true);
                return true;
            }
            finally { if (!isDirectory && File.Exists(target)) File.Delete(target); }
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] PullFileAsync failed"); return false; }
    }

    public async Task<bool> PushFileAsync(string udid, string localPath, string remotePath)
        => await PushFileAsync(udid, localPath, remotePath, CancellationToken.None);

    public async Task<bool> PushFileAsync(string udid, string localPath, string remotePath, CancellationToken cancellationToken)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !IsSafePath(remotePath) || !PathHelper.IsSafeLocalPath(localPath))
        { AppLogger.Log.Warn("[IosService] Unsafe path rejected"); return false; }
        try
        {
            var result = await RunAsync(udid, $"afc push {Quote(localPath)} {Quote(remotePath)}", 300000, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] PushFileAsync failed"); return false; }
    }

    public async Task<bool> DeleteFileAsync(string udid, string path)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !IsSafePath(path))
        { AppLogger.Log.Warn("[IosService] Unsafe path rejected"); return false; }
        try
        {
            var result = await RunAsync(udid, $"afc rm {Quote(path)}", InfoTimeoutMs).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] DeleteFileAsync failed"); return false; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  P1 — pymobiledevice3-exclusive features
    // ═══════════════════════════════════════════════════════════════

    public async Task<List<string>> ListCrashLogsAsync(string udid)
    {
        var logs = new List<string>();
        try
        {
            var result = await RunAsync(udid, "crash ls", DefaultTimeoutMs).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return logs;
            foreach (var raw in result.Output.Split('\n', '\r'))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line == "." || line == "..") continue;
                logs.Add(line);
            }
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] ListCrashLogsAsync failed"); }
        return logs;
    }

    public async Task<bool> PullCrashLogAsync(string udid, string crashName, string outputPath)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !IsSafePath(crashName) || !PathHelper.IsSafeLocalPath(outputPath))
            return false;
        try
        {
            var dir = Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var stagingDir = Path.Combine(dir, $".logpro-crash-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);
            try
            {
                var result = await RunAsync(udid, $"crash pull --remote-file {Quote(crashName)} {Quote(stagingDir)}", 30000).ConfigureAwait(false);
                if (!result.Success) return false;
                var pulled = Path.Combine(stagingDir, Path.GetFileName(crashName));
                if (!File.Exists(pulled) || !PathHelper.IsSafeLocalPath(pulled)) return false;
                File.Move(pulled, outputPath, overwrite: true);
                return File.Exists(outputPath);
            }
            finally
            {
                var resolvedParent = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var resolvedStaging = Path.GetFullPath(stagingDir);
                if (resolvedStaging.StartsWith(resolvedParent, StringComparison.OrdinalIgnoreCase) &&
                    PathHelper.IsSafeLocalPath(resolvedStaging) && Directory.Exists(resolvedStaging))
                {
                    try { Directory.Delete(resolvedStaging, recursive: true); }
                    catch (Exception ex) { AppLogger.Log.Warn(ex, "[IosService] Could not clean crash-log staging directory"); }
                }
            }
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] PullCrashLogAsync failed"); return false; }
    }

    public async Task<string> GetDiagnosticsAsync(string udid)
    {
        try
        {
            var result = await RunAsync(udid, "diagnostics info", 30000).ConfigureAwait(false);
            return result.Success ? (result.Output ?? "") : (result.Error ?? "Diagnostics failed");
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] GetDiagnosticsAsync failed"); return ex.Message; }
    }

    /// <summary>
    /// A user-visible iOS notification requires an installed app and notification permission.
    /// The CLI's `notification post` only posts a Darwin notification name, so it cannot
    /// satisfy this method's title/body contract.
    /// </summary>
    public Task<bool> SendNotificationAsync(string udid, string title, string body)
    {
        AppLogger.Log.Warn("[IosService] User-visible notifications are unsupported on iOS without an app integration");
        return Task.FromResult(false);
    }

    public async Task<List<DeviceInfo>> DiscoverNetworkDevicesAsync()
    {
        if (SecurityHelper.OfflineOnly)
        {
            AppLogger.Log.Warn("[IosService] Network device discovery blocked by offline security policy");
            return new List<DeviceInfo>();
        }

        var devices = new List<DeviceInfo>();
        try
        {
            var result = await RunAsync(null, "usbmux list --network", InfoTimeoutMs).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return devices;
            var output = result.Output.TrimStart();
            if (!output.StartsWith("[")) return devices;

            using var json = JsonDocument.Parse(output);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var udid = item.TryGetProperty("UniqueDeviceID", out var id) ? id.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(udid)) continue;
                devices.Add(new DeviceInfo
                {
                    Serial = udid,
                    Id = udid,
                    Platform = DevicePlatform.iOS,
                    Name = item.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? "" : "",
                    ConnectionState = DeviceConnectionState.Online
                });
            }
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] DiscoverNetworkDevicesAsync failed"); }
        return devices;
    }

    // ═══════════════════════════════════════════════════════════════
    //  P2 — Developer-mode features (require Developer Mode + DDI)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// pymobiledevice3 `developer shell` opens an interactive IPython REPL — not pipeable.
    /// Returns the long-running process; callers must drive stdin themselves.
    /// </summary>
    public async Task<System.Diagnostics.Process?> StartDeveloperShellAsync(string udid)
    {
        if (SecurityHelper.OfflineOnly) return null;
        try { return await StartLongAsync(udid, "developer shell").ConfigureAwait(false); }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] StartDeveloperShell failed"); return null; }
    }

    /// <summary>
    /// Screen recording via pymobiledevice3 is not supported (no equivalent CLI subcommand).
    /// Returns null and logs a warning so callers can surface a clear "not supported" message.
    /// </summary>
    public System.Diagnostics.Process? StartScreenRecording(string udid, string outputPath)
    {
        AppLogger.Log.Warn("[IosService] StartScreenRecording not supported by pymobiledevice3");
        return null;
    }

    /// <summary>
    /// The bundled pymobiledevice3 CLI has no URL-opening command.
    /// </summary>
    public Task<bool> OpenUrlAsync(string udid, string url)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !SecurityHelper.IsOfflineSafeUri(url))
        {
            AppLogger.Log.Warn("[IosService] Network URL blocked by offline security policy");
            return Task.FromResult(false);
        }
        AppLogger.Log.Warn("[IosService] URL opening is unsupported by the bundled pymobiledevice3 CLI");
        return Task.FromResult(false);
    }
    /// <summary>
    /// Resolves an app's container directory via `apps query`.
    /// Returns Container path string or "" on failure.
    /// </summary>
    public async Task<string> GetAppContainerPathAsync(string udid, string bundleId)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !SecurityHelper.IsValidBundleId(bundleId)) return "";
        try
        {
            var result = await RunAsync(udid, $"apps query {Quote(bundleId)}", InfoTimeoutMs).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return "";
            // Look for "Container" key in the dict-like output
            var match = System.Text.RegularExpressions.Regex.Match(
                result.Output,
                @"['""]?Container['""]?\s*[:=]\s*(?:'(?<v1>[^']*)'|""(?<v2>[^""]*)""|(?<v3>[^\r\n}]+))");
            var containerPath = match.Success
                ? (match.Groups["v1"].Success ? match.Groups["v1"].Value
                 : match.Groups["v2"].Success ? match.Groups["v2"].Value
                 : match.Groups["v3"].Value).Trim()
                : "";
            return containerPath;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] GetAppContainerPathAsync failed"); return ""; }
    }

    public async Task<bool> PullAppFileAsync(string udid, string bundleId, string remotePath, string localPath,
        CancellationToken cancellationToken = default)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !SecurityHelper.IsValidBundleId(bundleId) ||
            !IsSafePath(remotePath) || !PathHelper.IsSafeLocalPath(localPath)) return false;
        var isDirectory = Directory.Exists(localPath);
        var target = isDirectory ? localPath : localPath + ".logpro-part-" + Guid.NewGuid().ToString("N");
        try
        {
            var result = await RunAsync(udid, $"apps pull {Quote(bundleId)} {Quote(remotePath)} {Quote(target)}",
                300000, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success) return false;
            if (isDirectory) return Directory.Exists(Path.Combine(localPath, Path.GetFileName(remotePath.TrimEnd('/'))));
            if (!File.Exists(target)) return false;
            File.Move(target, localPath, true);
            return true;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] PullAppFileAsync failed"); return false; }
        finally { if (!isDirectory && File.Exists(target)) File.Delete(target); }
    }

    public async Task<bool> PushAppFileAsync(string udid, string bundleId, string localPath, string remotePath,
        CancellationToken cancellationToken = default)
    {
        if (!SecurityHelper.IsValidOfflineDeviceSelector(udid) || !SecurityHelper.IsValidBundleId(bundleId) ||
            !IsSafePath(remotePath) || !PathHelper.IsSafeLocalPath(localPath) || !File.Exists(localPath)) return false;
        try
        {
            var result = await RunAsync(udid, $"apps push {Quote(bundleId)} {Quote(localPath)} {Quote(remotePath)}",
                300000, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] PushAppFileAsync failed"); return false; }
    }

    private static bool IsSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!path.StartsWith('/') || path.Split('/').Any(segment => segment is "." or "..")) return false;
        return !path.Any(char.IsControl);
    }
}
