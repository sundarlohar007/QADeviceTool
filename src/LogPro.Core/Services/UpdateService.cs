using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Checks upstream repositories for newer versions of bundled tools and LogPro itself.
/// Downloads, verifies (SHA-256), and installs updates into the tools/ directory.
///
/// SECURITY: This service is part of the Tool Channel — it downloads binaries only.
/// It never sends device data, log content, or any request body. All requests are
/// anonymous GET calls with no query parameters containing user/device information.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly HttpClient _http = CreateHttpClient();
    private readonly string _toolsDir;
    private readonly string _appDir;

    /// <summary>Known upstream sources for each tool.</summary>
    private static readonly Dictionary<string, ToolSource> _sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adb"] = ManagedSource("adb"),
        ["scrcpy"] = ManagedSource("scrcpy"),
        ["pymobiledevice3"] = ManagedSource("pymobiledevice3"),
        ["logpro"] = new ToolSource
        {
            GitHubOwner = "sundarlohar007",
            GitHubRepo = "QADeviceTool",
            AssetPattern = @"^LogPro_v[\d.]+\.exe$",
            VersionPattern = @"v?([\d.]+)"
        }
    };

    private static ToolSource ManagedSource(string name) => new()
    {
        GitHubOwner = "sundarlohar007",
        GitHubRepo = "QADeviceTool",
        AssetPattern = $@"^LogPro-tool-{name}-([\d.]+)-win-x64\.zip$",
        VersionPattern = @"v?([\d.]+)",
        SubDirectory = name
    };

    public static bool RequiresElevation
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return !new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    public UpdateService(string? toolsDir = null, string? appDir = null)
    {
        _appDir = appDir ?? AppContext.BaseDirectory;
        _toolsDir = toolsDir ?? Path.Combine(_appDir, "tools");
    }

    /// <summary>
    /// Checks all known tools for available updates. Returns one <see cref="UpdateInfo"/>
    /// per tool, regardless of whether an update is available (check <see cref="UpdateInfo.IsNewerAvailable"/>).
    /// </summary>
    public async Task<List<UpdateInfo>> CheckAllAsync(CancellationToken ct = default)
    {
        async Task<UpdateInfo> Check(string toolName, Func<Task<UpdateInfo>> check)
        {
            try
            {
                return await check().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                AppLogger.Log.Debug(ex, $"[UpdateService] Failed to check {toolName}");
                return new UpdateInfo
                {
                    ToolName = toolName,
                    CurrentVersion = GetCurrentVersion(toolName),
                    LatestVersion = "",
                    ReleaseNotes = $"Check failed: {ex.Message}"
                };
            }
        }
        var checks = new[]
        {
            Check("adb", () => CheckAdbAsync(ct)),
            Check("scrcpy", () => CheckScrcpyAsync(ct)),
            Check("pymobiledevice3", () => CheckPymobiledevice3Async(ct)),
            Check("logpro", async () => await CheckOneAsync("logpro", _sources["logpro"], await GetReleaseJsonAsync(ct)).ConfigureAwait(false))
        };
        return (await Task.WhenAll(checks).ConfigureAwait(false)).ToList();
    }

    private async Task<UpdateInfo> CheckAdbAsync(CancellationToken ct)
    {
        const string url = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var cache = GetSecuredUpdateCache();
        var partial = Path.Combine(cache, "google-platform-tools." + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await DownloadFileAsync(url, partial, null, deadline.Token).ConfigureAwait(false);
            var parsed = ReadAdbArchiveVersion(partial);
            var hash = await ComputeSha256Async(partial).ConfigureAwait(false);
            File.Move(partial, Path.Combine(cache, hash + ".download"), overwrite: true);
            return new UpdateInfo
            {
                ToolName = "adb",
                CurrentVersion = GetCurrentVersion("adb"),
                LatestVersion = ToManagedVersion(parsed),
                DownloadUrl = url,
                Sha256 = hash,
                FileName = "platform-tools-latest-windows.zip",
                ReleaseNotes = "Official Google Platform-Tools for Windows."
            };
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private async Task<UpdateInfo> CheckScrcpyAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("https://api.github.com/repos/Genymobile/scrcpy/releases/latest", ct).ConfigureAwait(false);
        return ParseScrcpyRelease(json, GetCurrentVersion("scrcpy"));
    }

    internal static UpdateInfo ParseScrcpyRelease(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var version = Regex.Match(tag, @"^v?(\d+(?:\.\d+){1,3})$").Groups[1].Value;
        if (!Version.TryParse(version, out var parsed)) throw new InvalidDataException("scrcpy release has no valid version.");
        var expectedName = "scrcpy-win64-v" + version + ".zip";
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != expectedName) continue;
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var update = new UpdateInfo
            {
                ToolName = "scrcpy",
                CurrentVersion = currentVersion,
                LatestVersion = ToManagedVersion(parsed),
                DownloadUrl = url,
                Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "",
                FileName = expectedName,
                ReleaseNotes = "Official Genymobile Windows release."
            };
            if (!IsTrustedDownloadUrl(update) ||
                new Uri(url).AbsolutePath != $"/Genymobile/scrcpy/releases/download/v{version}/{expectedName}" ||
                !Regex.IsMatch(update.Sha256, "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("scrcpy release asset has no trusted URL or SHA-256 digest.");
            return update;
        }
        throw new InvalidDataException("scrcpy release has no matching Windows x64 archive.");
    }

    private async Task<UpdateInfo> CheckPymobiledevice3Async(CancellationToken ct)
    {
        var json = await GetJsonAsync("https://pypi.org/pypi/pymobiledevice3/json", ct).ConfigureAwait(false);
        return ParsePymobiledevice3Release(json, GetCurrentVersion("pymobiledevice3"));
    }

    internal static UpdateInfo ParsePymobiledevice3Release(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var version = doc.RootElement.GetProperty("info").GetProperty("version").GetString() ?? "";
        if (!Version.TryParse(version, out var parsed)) throw new InvalidDataException("PyPI did not provide a compatible version number.");
        return new UpdateInfo
        {
            ToolName = "pymobiledevice3",
            CurrentVersion = currentVersion,
            LatestVersion = ToManagedVersion(parsed),
            ReleaseNotes = "A newer upstream Python package needs a tested, frozen LogPro Windows runtime. Keep the bundled version until a compatible installer is released."
        };
    }

    private static string ToManagedVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{Math.Max(0, version.Revision)}";

    internal static Version ReadAdbArchiveVersion(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var properties = archive.GetEntry("platform-tools/source.properties")
            ?? throw new InvalidDataException("Google platform-tools archive lacks version metadata.");
        using var reader = new StreamReader(properties.Open());
        var versionText = reader.ReadToEnd();
        var version = Regex.Match(versionText, @"(?m)^Pkg\.Revision\s*=\s*([\d.]+)\s*$").Groups[1].Value;
        if (!Version.TryParse(version, out var parsed) || archive.GetEntry("platform-tools/adb.exe") == null)
            throw new InvalidDataException("Google platform-tools archive is incomplete.");
        return parsed;
    }

    private static string GetSecuredUpdateCache()
    {
        var cache = Path.Combine(PathHelper.GetAppDataDirectory(), "updates");
        Directory.CreateDirectory(cache);
        if (!PathHelper.RestrictDirectoryAccess(cache)) throw new IOException("Cannot secure update cache.");
        return cache;
    }

    public async Task<string> PrepareUpdateAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (!update.IsInstallable || !IsTrustedDownloadUrl(update))
            throw new InvalidDataException("No verified package from an approved source is available.");
        var cache = GetSecuredUpdateCache();
        var target = Path.Combine(cache, update.Sha256.ToLowerInvariant() + ".download");
        if (File.Exists(target) && string.Equals(await ComputeSha256Async(target), update.Sha256, StringComparison.OrdinalIgnoreCase)) return target;
        var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await DownloadFileAsync(update.DownloadUrl, partial, null, ct).ConfigureAwait(false);
            if (!string.Equals(await ComputeSha256Async(partial), update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update checksum verification failed.");
            File.Move(partial, target, true);
            return target;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    /// <summary>
    /// Downloads and installs an update for a specific tool. Returns true on success.
    /// The download is SHA-256 verified before it can be installed.
    /// </summary>
    public async Task<(bool Success, string Message)> ApplyUpdateAsync(UpdateInfo update, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!update.IsNewerAvailable && update.CurrentVersion != "unknown")
            return (false, "No update available.");

        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            return (false, "No download URL.");
        if (!Regex.IsMatch(update.Sha256 ?? string.Empty, "^[a-fA-F0-9]{64}$"))
            return (false, "No trusted SHA-256 digest is available for this release asset.");
        if (!IsTrustedDownloadUrl(update)) return (false, "Update asset is not from the approved source.");

        if (ToolLauncher.HasRunningTools)
            return (false, "Update deferred: stop active device operations before installing tools.");
        if (update.ToolName != "logpro" && RequiresElevation &&
            Path.GetFullPath(_appDir).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            return (false, "Administrator access is required. Use Install Selected to open the Windows updater.");
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"logpro_update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. Download
                var fileName = update.FileName;
                if (string.IsNullOrEmpty(fileName))
                    fileName = Path.GetFileName(new Uri(update.DownloadUrl).LocalPath);
                if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return (false, "Invalid update asset filename.");
                var downloadPath = Path.Combine(tempDir, fileName);

                AppLogger.Log.Info($"[UpdateService] Downloading {update.ToolName} v{update.LatestVersion} from {update.DownloadUrl}");
                var prepared = await PrepareUpdateAsync(update, ct).ConfigureAwait(false);
                File.Copy(prepared, downloadPath);
                progress?.Report(100);

                // 2. Verify the upstream SHA-256 digest before installation.
                {
                    var actualHash = await ComputeSha256Async(downloadPath).ConfigureAwait(false);
                    if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"SHA-256 mismatch: expected {update.Sha256}, got {actualHash}. Download may be corrupted or tampered.");
                    }
                    AppLogger.Log.Info($"[UpdateService] SHA-256 verified for {update.ToolName}");
                }

                // 3. Handle self-update (LogPro itself)
                if (string.Equals(update.ToolName, "logpro", StringComparison.OrdinalIgnoreCase))
                {
                    return HandleSelfUpdate(downloadPath);
                }

                // 4. Extract/install into tools directory
                if (!_sources.TryGetValue(update.ToolName, out var source))
                    return (false, $"Unknown tool: {update.ToolName}");

                await InstallToolAsync(downloadPath, update.ToolName, update.LatestVersion, ct).ConfigureAwait(false);

                AppLogger.Log.Info($"[UpdateService] Successfully updated {update.ToolName} to v{update.LatestVersion}");
                return (true, $"{update.ToolName} updated to v{update.LatestVersion}");
            }
            finally
            {
                // Clean up temp directory
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { /* best effort */ }
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "Update cancelled.");
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[UpdateService] Failed to apply update for {update.ToolName}");
            return (false, $"Update failed: {ex.Message}");
        }
    }

    // ─── Private helpers ─────────────────────────────────────────

    private static async Task<string> GetReleaseJsonAsync(CancellationToken ct)
        => await GetJsonAsync("https://api.github.com/repos/sundarlohar007/QADeviceTool/releases/latest", ct).ConfigureAwait(false);

    private static async Task<string> GetJsonAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    internal static bool IsTrustedDownloadUrl(UpdateInfo update)
    {
        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment)) return false;
        return update.ToolName switch
        {
            "adb" => uri.Host == "dl.google.com" && uri.AbsolutePath == "/android/repository/platform-tools-latest-windows.zip",
            "scrcpy" => uri.Host == "github.com" && Regex.IsMatch(uri.AbsolutePath,
                @"^/Genymobile/scrcpy/releases/download/v[\d.]+/scrcpy-win64-v[\d.]+\.zip$", RegexOptions.IgnoreCase),
            "logpro" => uri.Host == "github.com" && uri.AbsolutePath.StartsWith(
                "/sundarlohar007/QADeviceTool/releases/download/", StringComparison.Ordinal) &&
                IsCompatibleAsset("logpro", Path.GetFileName(uri.AbsolutePath)),
            _ => false
        };
    }

    private Task<UpdateInfo> CheckOneAsync(string toolName, ToolSource source, string json)
    {
        var currentVersion = GetCurrentVersion(toolName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "";
        var releaseNotes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
        var versionMatch = Regex.Match(tagName, source.VersionPattern);
        var latestVersion = versionMatch.Success ? versionMatch.Groups[1].Value : tagName;

        // Find matching asset
        string downloadUrl = "", sha256 = "", fileName = "";
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (IsCompatibleAsset(toolName, name))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    fileName = name;
                    if (toolName != "logpro") latestVersion = Regex.Match(name, source.AssetPattern).Groups[1].Value;
                    if (asset.TryGetProperty("digest", out var digest))
                    {
                        var value = digest.GetString() ?? "";
                        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            sha256 = value[7..];
                    }
                    break;
                }
            }
        }

        if (toolName != "logpro" && string.IsNullOrEmpty(fileName)) latestVersion = "";
        return Task.FromResult(new UpdateInfo
        {
            ToolName = toolName,
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            DownloadUrl = downloadUrl,
            Sha256 = sha256,
            ReleaseNotes = string.IsNullOrEmpty(fileName) ? "Check failed: no compatible Windows package was published for this component." : TruncateReleaseNotes(releaseNotes),
            FileName = fileName
        });
    }

    internal static bool IsCompatibleAsset(string toolName, string assetName) =>
        _sources.TryGetValue(toolName, out var source) && Regex.IsMatch(assetName, source.AssetPattern, RegexOptions.IgnoreCase);

    private string GetCurrentVersion(string toolName)
    {
        try
        {
            var versionFile = Path.Combine(_toolsDir, toolName, "tool-package-version.txt");
            if (!File.Exists(versionFile)) versionFile = Path.Combine(_toolsDir, toolName, "tool-version.txt");
            if (File.Exists(versionFile)) return File.ReadAllText(versionFile).Trim();
            if (string.Equals(toolName, "logpro", StringComparison.OrdinalIgnoreCase))
            {
                return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
            }

            if (string.Equals(toolName, "scrcpy", StringComparison.OrdinalIgnoreCase))
            {
                // Detect version from directory name: scrcpy-win64-v3.3.4
                if (Directory.Exists(_toolsDir))
                {
                    var dir = Directory.GetDirectories(_toolsDir, "scrcpy-win64-*").FirstOrDefault();
                    if (dir != null)
                    {
                        var match = Regex.Match(Path.GetFileName(dir), @"v([\d.]+)");
                        if (match.Success) return match.Groups[1].Value;
                    }
                }
            }

            if (string.Equals(toolName, "pymobiledevice3", StringComparison.OrdinalIgnoreCase))
            {
                var exe = Path.Combine(_toolsDir, "pymobiledevice3", "pymobiledevice3.exe");
                if (File.Exists(exe))
                {
                    var ver = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                    if (!string.IsNullOrEmpty(ver.ProductVersion)) return ver.ProductVersion;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log.Debug(ex, $"[UpdateService] Failed to detect current version of {toolName}");
        }
        return "unknown";
    }

    internal async Task InstallToolAsync(string downloadPath, string toolName, string expectedVersion, CancellationToken ct)
    {
        if (toolName is not ("adb" or "scrcpy" or "pymobiledevice3")) throw new ArgumentException("Unknown managed tool.", nameof(toolName));
        await new ToolUpdateTransaction(_appDir, _toolsDir).RecoverAsync(toolName).ConfigureAwait(false);
        var integrity = await ToolManifest.VerifyAsync(_toolsDir, Path.Combine(_appDir, ToolManifest.DefaultFileName)).ConfigureAwait(false);
        if (!integrity.IsHealthy) throw new InvalidDataException("Repair the existing installation before updating tools. Its integrity check failed.");
        Directory.CreateDirectory(_toolsDir);
        var staging = Path.Combine(_appDir, $".update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Managed tool updates must contain a complete Windows runtime bundle.");
            ZipFile.ExtractToDirectory(downloadPath, staging);
            var staged = Path.Combine(staging, toolName);
            var upstream = expectedVersion.EndsWith(".0", StringComparison.Ordinal)
                ? expectedVersion[..^2] : expectedVersion;
            if (toolName == "adb" && Directory.Exists(Path.Combine(staging, "platform-tools")))
            {
                Directory.Move(Path.Combine(staging, "platform-tools"), staged);
                var sourceProperties = Path.Combine(staged, "source.properties");
                if (!File.Exists(sourceProperties) || !Regex.IsMatch(File.ReadAllText(sourceProperties),
                    @"(?m)^Pkg\.Revision\s*=\s*" + Regex.Escape(upstream) + @"\s*$"))
                    throw new InvalidDataException("Google archive revision differs from the checked release.");
                var helper = Path.Combine(_toolsDir, "adb", "inventory.jar");
                if (File.Exists(helper)) File.Copy(helper, Path.Combine(staged, "inventory.jar"));
                File.WriteAllText(Path.Combine(staged, "tool-version.txt"), upstream);
                File.WriteAllText(Path.Combine(staged, "tool-package-version.txt"), expectedVersion);
            }
            else if (toolName == "scrcpy" && !Directory.Exists(staged))
            {
                var source = Path.Combine(staging, "scrcpy-win64-v" + upstream);
                if (!Directory.Exists(source)) throw new InvalidDataException("scrcpy archive layout or version is unexpected.");
                Directory.Move(source, staged);
                File.WriteAllText(Path.Combine(staged, "tool-version.txt"), upstream);
                File.WriteAllText(Path.Combine(staged, "tool-package-version.txt"), expectedVersion);
            }
            if (!File.Exists(Path.Combine(staged, toolName + ".exe")) ||
                !File.Exists(Path.Combine(staged, "tool-version.txt")))
                throw new InvalidDataException("Update archive is missing its executable or version metadata.");
            var packageVersionFile = Path.Combine(staged, "tool-package-version.txt");
            if (!File.Exists(packageVersionFile)) packageVersionFile = Path.Combine(staged, "tool-version.txt");
            if (!string.Equals(File.ReadAllText(packageVersionFile).Trim(), expectedVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Package version does not match the release metadata.");
            // Validate the CLI contract before touching the working installation.
            var args = toolName == "scrcpy" ? "--version" : "version";
            var probe = await ToolLauncher.RunAsync(Path.Combine(staged, toolName + ".exe"), args, 60000, cancellationToken: ct).ConfigureAwait(false);
            if (!probe.Success) throw new InvalidDataException("Updated tool health check failed: " + probe.Error);
            ct.ThrowIfCancellationRequested();
            await new ToolUpdateTransaction(_appDir, _toolsDir).CommitAsync(toolName, staged).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception ex) { AppLogger.Log.Debug(ex, "[UpdateService] Could not remove staging directory"); }
        }
    }

    private string BackupPath(string toolName)
        => Path.Combine(_appDir, $".logpro_backup_{toolName}");

    public bool HasRollback(string toolName) => toolName is ("adb" or "scrcpy" or "pymobiledevice3") &&
        Directory.Exists(BackupPath(toolName)) && File.Exists(BackupPath(toolName) + ".name") && File.Exists(BackupPath(toolName) + ".manifest.json");

    public async Task<(bool Success, string Message)> RollbackLastUpdateAsync(string toolName)
    {
        if (ToolLauncher.HasRunningTools) return (false, "Stop device operations before rolling back tools.");
        if (toolName is not ("adb" or "scrcpy" or "pymobiledevice3")) return (false, "Unknown tool.");
        var staging = Path.Combine(_appDir, ".rollback_" + Guid.NewGuid().ToString("N"));
        try
        {
            var transaction = new ToolUpdateTransaction(_appDir, _toolsDir);
            await transaction.RecoverAsync(toolName).ConfigureAwait(false);
            await transaction.ValidateBackupAsync(toolName).ConfigureAwait(false);
            var integrity = await ToolManifest.VerifyAsync(_toolsDir, Path.Combine(_appDir, ToolManifest.DefaultFileName)).ConfigureAwait(false);
            bool BelongsToTool(string path) => path.StartsWith(toolName + "/", StringComparison.OrdinalIgnoreCase) ||
                (toolName == "scrcpy" && path.StartsWith("scrcpy-win64-", StringComparison.OrdinalIgnoreCase));
            if (integrity.Mismatched.Select(e => e.Path).Concat(integrity.Missing).Concat(integrity.Unexpected).Any(p => !BelongsToTool(p)))
                throw new InvalidDataException("Unrelated installed files fail verification. Repair the complete installation first.");
            ToolUpdateTransaction.CopyTree(BackupPath(toolName), staging);
            await transaction.CommitAsync(toolName, staging, preserveBackup: true).ConfigureAwait(false);
            return (true, $"Restored previous {toolName} installation.");
        }
        catch (Exception ex)
        {
            AppLogger.Log.Error(ex, $"[UpdateService] Rollback failed for {toolName}");
            return (false, $"Rollback failed: {ex.Message}");
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public async Task RecoverPendingTransactionsAsync()
    {
        var transaction = new ToolUpdateTransaction(_appDir, _toolsDir);
        foreach (var tool in new[] { "adb", "scrcpy", "pymobiledevice3" })
            await transaction.RecoverAsync(tool).ConfigureAwait(false);
    }

    public bool HasPendingTransactions => new[] { "adb", "scrcpy", "pymobiledevice3" }
        .Any(tool => File.Exists(Path.Combine(_appDir, ".logpro_transaction_" + tool + ".json")));

    public async Task<(bool Success, string Message)> ValidateRollbackAsync(string toolName)
    {
        if (ToolLauncher.HasRunningTools) return (false, "Stop device operations before rolling back tools.");
        try
        {
            await new ToolUpdateTransaction(_appDir, _toolsDir).ValidateBackupAsync(toolName).ConfigureAwait(false);
            return (true, "Previous package verified.");
        }
        catch (Exception ex) { return (false, "Previous package cannot be restored: " + SecurityHelper.RedactSensitiveText(ex.Message)); }
    }

    private static (bool Success, string Message) HandleSelfUpdate(string installerPath)
    {
        try
        {
            // The caller removes its download directory on return. Keep a verified copy
            // alive so the installer can still read it after this process exits.
            var retainedInstaller = Path.Combine(Path.GetTempPath(), $"logpro_verified_setup_{Guid.NewGuid():N}.exe");
            File.Copy(installerPath, retainedInstaller);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = retainedInstaller,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            AppLogger.Log.Info("[UpdateService] Self-update installer launched. Application should be restarted.");
            return (true, "Installer launched. Please restart LogPro after installation completes.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to launch installer: {ex.Message}");
        }
    }

    private static async Task DownloadFileAsync(string url, string outputPath, IProgress<int>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = File.Create(outputPath);

        const long maxUpdateBytes = 500L * 1024 * 1024;
        if (totalBytes > maxUpdateBytes) throw new InvalidDataException("Update archive exceeds the size limit.");
        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            totalRead += bytesRead;
            if (totalRead > maxUpdateBytes) throw new InvalidDataException("Update archive exceeds the size limit.");
            if (totalBytes > 0)
                progress?.Report((int)(totalRead * 100 / totalBytes));
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string TruncateReleaseNotes(string notes, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(notes) || notes.Length <= maxLength)
            return notes;
        return notes[..maxLength] + "...";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LogPro-UpdateChecker/1.0");
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    public void Dispose() { /* HttpClient is static, intentionally not disposed */ }

    private sealed class ToolSource
    {
        public string GitHubOwner { get; init; } = "";
        public string GitHubRepo { get; init; } = "";
        public string AssetPattern { get; init; } = "";
        public string VersionPattern { get; init; } = "";
        public string? SubDirectory { get; init; }
    }
}
