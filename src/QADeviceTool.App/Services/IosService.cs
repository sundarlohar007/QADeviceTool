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
    private readonly string _exe;
    private readonly bool _isModuleInvocation;
    private readonly string _toolKind;
    private static readonly SemaphoreSlim _ipcLock = new(1, 1);

    private const int DefaultTimeoutMs = 15000;
    private const int InfoTimeoutMs = 10000;
    private const int InstallTimeoutMs = 600000;

    public IosService()
    {
        var bundled = ResolveBundledExe();
        if (bundled != null && ProbeBundledExe(bundled))
        {
            _exe = bundled;
            _isModuleInvocation = false;
            _toolKind = $"bundled ({bundled})";
        }
        else
        {
            _exe = ResolveSystemPython() ?? "python";
            _isModuleInvocation = true;
            _toolKind = $"python -m pymobiledevice3 ({_exe})";
        }
        AppLogger.Log.Info($"[IosService] Using {_toolKind}");
    }

    private static string? ResolveBundledExe()
    {
        var path = Path.Combine(ToolLauncher.ToolsDirectory, "pymobiledevice3", "pymobiledevice3.exe");
        return File.Exists(path) ? path : null;
    }

    private static bool ProbeBundledExe(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                Arguments = "version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLogger.Log.Warn(ex, "[IosService] Bundled pymobiledevice3.exe probe failed");
            return false;
        }
    }

    private static string? ResolveSystemPython()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathVar.Split(';')
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.Combine(p, "python.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Builds full argument string with optional UDID flag.</summary>
    private string BuildArgs(string? udid, string subcommand)
        => BuildCommandArgs(_isModuleInvocation, udid, subcommand);

    internal static string BuildCommandArgs(bool isModuleInvocation, string? udid, string subcommand)
    {
        var udidFlag = string.IsNullOrEmpty(udid) ? "" : $" --udid {Quote(udid)}";
        var prefix = isModuleInvocation ? "-m pymobiledevice3 " : "";
        return $"{prefix}--no-color {subcommand}{udidFlag}";
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";

    private async Task<ToolLauncherResult> RunAsync(string? udid, string subcommand, int timeoutMs = DefaultTimeoutMs, Action<string>? outputCallback = null)
    {
        await _ipcLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ToolLauncher.RunAsync(_exe, BuildArgs(udid, subcommand), timeoutMs, outputCallback).ConfigureAwait(false);
        }
        finally
        {
            _ipcLock.Release();
        }
    }

    private System.Diagnostics.Process? StartLong(string? udid, string subcommand)
        => ToolLauncher.StartLongRunning(_exe, BuildArgs(udid, subcommand));

    public Task<ToolLauncherResult> ExecuteCommandAsync(string? udid, string subcommand, int timeoutMs = DefaultTimeoutMs, Action<string>? outputCallback = null)
        => RunAsync(udid, subcommand, timeoutMs, outputCallback);

    public async Task<ToolStatus> CheckAvailabilityAsync()
    {
        try
        {
            var result = await RunAsync(null, "version", InfoTimeoutMs).ConfigureAwait(false);
            var statusMsg = result.Success
                ? $"Ready — {_toolKind}"
                : $"Failed (exit={result.ExitCode}): {result.Error?.Trim() ?? result.Output?.Trim() ?? "unknown"}";
            return new ToolStatus
            {
                Name = "pymobiledevice3 (iOS Tools)",
                Description = "Required for iOS device communication",
                IsInstalled = result.Success,
                Version = result.Success ? (result.Output?.Trim() ?? "unknown") : "n/a",
                Path = _exe,
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
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var result = await RunAsync(null, "usbmux list", InfoTimeoutMs).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return devices;

            var output = result.Output.TrimStart();
            if (!output.StartsWith("[")) return devices;

            using var json = JsonDocument.Parse(output);
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
                    Serial = udid, Id = udid,
                    Name = name, Model = model, OsVersion = osVer,
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
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, "[IosService] GetConnectedDevicesAsync failed");
        }
        return devices;
    }

    public async Task<DeviceInfo> GetDeviceDetailsAsync(DeviceInfo device)
    {
        try
        {
            var result = await RunAsync(device.Serial, "lockdown info", InfoTimeoutMs).ConfigureAwait(false);
            if (!result.Success)
            {
                if ((result.Error ?? "").Contains("trust", StringComparison.OrdinalIgnoreCase) ||
                    (result.Error ?? "").Contains("paired", StringComparison.OrdinalIgnoreCase))
                    device.ConnectionState = DeviceConnectionState.PendingTrust;
                return device;
            }

            ParseLockdownInfo(result.Output ?? "", device);
            if (string.IsNullOrEmpty(device.Name)) device.Name = device.Model ?? "iOS Device";
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, $"[IosService] GetDeviceDetailsAsync failed for {device.Serial}"); }
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
            catch { /* fall through to regex */ }
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

    public System.Diagnostics.Process? StartLogCapture(string udid, string outputFilePath)
    {
        try
        {
            // syslog live streams to stdout by default; SessionService reads stdout and
            // writes the file itself. Using --out would bypass the capture pipeline entirely.
            return StartLong(udid, "syslog live");
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] StartLogCapture failed"); return null; }
    }

    public async Task<bool> CaptureScreenshotAsync(string udid, string outputPath)
    {
        try
        {
            // developer screenshot uses the deprecated lockdown screenshot service — works without DeveloperDiskImage.
            var result = await RunAsync(udid, $"developer screenshot {Quote(outputPath)}", DefaultTimeoutMs).ConfigureAwait(false);
            return result.Success && File.Exists(outputPath);
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] CaptureScreenshotAsync failed"); return false; }
    }

    public async Task<(bool Success, string Message)> InstallIpaAsync(string udid, string ipaPath, Action<string>? outputCallback = null)
    {
        try
        {
            outputCallback?.Invoke($"Installing: {ipaPath}");
            var result = await RunAsync(udid, $"apps install {Quote(ipaPath)}", InstallTimeoutMs, outputCallback).ConfigureAwait(false);
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

    /// <summary>
    /// Parses `apps list` output. pymobiledevice3 emits a top-level dict keyed by bundle id.
    /// </summary>
    internal static List<AppItem> ParseAppsList(string output)
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
                        var name = info.TryGetProperty("CFBundleDisplayName", out var dn) ? dn.GetString() ?? pkg
                                 : info.TryGetProperty("CFBundleName", out var bn) ? bn.GetString() ?? pkg
                                 : pkg;
                        var ver = info.TryGetProperty("CFBundleShortVersionString", out var vs) ? vs.GetString() ?? ""
                                : info.TryGetProperty("CFBundleVersion", out var bv) ? bv.GetString() ?? "" : "";
                        apps.Add(new AppItem { PackageId = pkg, Name = name, Version = ver, Platform = DevicePlatform.iOS });
                    }
                    return apps;
                }
            }
            catch { /* fall through to text parse */ }
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
                    apps.Add(new AppItem { PackageId = currentPkg, Name = currentName ?? currentPkg, Version = currentVer ?? "", Platform = DevicePlatform.iOS });
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
            apps.Add(new AppItem { PackageId = currentPkg, Name = currentName ?? currentPkg, Version = currentVer ?? "", Platform = DevicePlatform.iOS });
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
        try
        {
            var result = await RunAsync(udid, $"apps uninstall {Quote(packageId)}", DefaultTimeoutMs).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] UninstallAppAsync failed"); return false; }
    }

    public async Task<List<DeviceFile>> ListDirectoryAsync(string udid, string path)
    {
        var files = new List<DeviceFile>();
        try
        {
            var result = await RunAsync(udid, $"afc ls {Quote(path)}", DefaultTimeoutMs).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return files;
            files = ParseAfcLs(result.Output, path);
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] ListDirectoryAsync failed"); }
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
            var rawName = line.Trim();
            if (string.IsNullOrEmpty(rawName)) continue;
            // Skip noise (dot entries, total lines)
            if (rawName == "." || rawName == "..") continue;

            var hadTrailingSlash = rawName.EndsWith("/");
            var normalized = rawName.Trim('/');
            var name = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalized;
            if (string.IsNullOrWhiteSpace(name)) continue;

            // AFC listings often omit trailing slash markers for directories.
            // Prefer navigability for dotless entries; opening a false-positive
            // file simply returns an empty/error listing instead of blocking browse.
            var isDir = hadTrailingSlash || !name.Contains('.');

            files.Add(new DeviceFile
            {
                Name = name,
                Path = CombineDevicePath(basePath, name),
                IsDirectory = isDir,
                Size = 0,
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
    {
        try
        {
            var result = await RunAsync(udid, $"afc pull {Quote(remotePath)} {Quote(localPath)}", 60000).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] PullFileAsync failed"); return false; }
    }

    public async Task<bool> PushFileAsync(string udid, string localPath, string remotePath)
    {
        try
        {
            var result = await RunAsync(udid, $"afc push {Quote(localPath)} {Quote(remotePath)}", 60000).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] PushFileAsync failed"); return false; }
    }

    public async Task<bool> DeleteFileAsync(string udid, string path)
    {
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
        try
        {
            // crash pull has signature: pull [--remote-file PATH] [OUT]; we use defaults and let it pull all,
            // then move the named file. Simpler: use afc-style targeted pull via crash pull subcommand if exposed.
            var dir = Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var result = await RunAsync(udid, $"crash pull {Quote(dir)} --remote-file {Quote(crashName)}", 30000).ConfigureAwait(false);
            return result.Success;
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
    /// Posts a Darwin notification name. pymobiledevice3 only supports posting the
    /// notification name (notify_post); the title/body params are not honored by pymd3.
    /// We pass `body` as the notification name when present, else `title`.
    /// </summary>
    public async Task<bool> SendNotificationAsync(string udid, string title, string body)
    {
        try
        {
            var name = !string.IsNullOrWhiteSpace(body) ? body : title;
            if (string.IsNullOrWhiteSpace(name)) return false;
            var result = await RunAsync(udid, $"notification post --insecure {Quote(name)}", InfoTimeoutMs).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception ex) { AppLogger.Log.Error(ex, "[IosService] SendNotificationAsync failed"); return false; }
    }

    public async Task<List<DeviceInfo>> DiscoverNetworkDevicesAsync()
    {
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
                    Serial = udid, Id = udid, Platform = DevicePlatform.iOS,
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
    public System.Diagnostics.Process? StartDeveloperShell(string udid)
    {
        try { return StartLong(udid, "developer shell"); }
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
    /// pymobiledevice3 has no direct openurl command. Always returns false.
    /// Use the Springboard launch path (DVT) on devices with Developer Mode enabled
    /// if URL launching is needed.
    /// </summary>
    public Task<bool> OpenUrlAsync(string udid, string url)
    {
        AppLogger.Log.Warn($"[IosService] OpenUrlAsync not supported by pymobiledevice3 (url={url})");
        return Task.FromResult(false);
    }

    /// <summary>
    /// Resolves an app's container directory via `apps query`.
    /// Returns Container path string or "" on failure.
    /// </summary>
    public async Task<string> GetAppContainerPathAsync(string udid, string bundleId)
    {
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
}
