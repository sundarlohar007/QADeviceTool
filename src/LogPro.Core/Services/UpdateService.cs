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
        ["scrcpy"] = new ToolSource
        {
            GitHubOwner = "Genymobile",
            GitHubRepo = "scrcpy",
            AssetPattern = @"scrcpy-win64-v[\d.]+\.zip",
            VersionPattern = @"v([\d.]+)",
            SubDirectory = "scrcpy-win64-*"
        },
        ["pymobiledevice3"] = new ToolSource
        {
            GitHubOwner = "doronz88",
            GitHubRepo = "pymobiledevice3",
            AssetPattern = @"pymobiledevice3.*\.exe",
            VersionPattern = @"([\d.]+)",
            SubDirectory = "pymobiledevice3"
        },
        ["logpro"] = new ToolSource
        {
            GitHubOwner = "sundarlohar007",
            GitHubRepo = "QADeviceTool",
            AssetPattern = @"Setup\.exe",
            VersionPattern = @"v?([\d.]+)",
            SubDirectory = null // self-update, not a tool subdirectory
        }
    };

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
        var results = new List<UpdateInfo>();
        foreach (var (toolName, source) in _sources)
        {
            try
            {
                var info = await CheckOneAsync(toolName, source, ct).ConfigureAwait(false);
                results.Add(info);
            }
            catch (Exception ex)
            {
                AppLogger.Log.Debug(ex, $"[UpdateService] Failed to check {toolName}");
                results.Add(new UpdateInfo
                {
                    ToolName = toolName,
                    CurrentVersion = GetCurrentVersion(toolName),
                    LatestVersion = "",
                    ReleaseNotes = $"Check failed: {ex.Message}"
                });
            }
        }
        return results;
    }

    /// <summary>
    /// Downloads and installs an update for a specific tool. Returns true on success.
    /// The download is SHA-256 verified if a checksum is available.
    /// </summary>
    public async Task<(bool Success, string Message)> ApplyUpdateAsync(UpdateInfo update, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!update.IsNewerAvailable)
            return (false, "No update available.");

        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            return (false, "No download URL.");

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
                var downloadPath = Path.Combine(tempDir, fileName);

                AppLogger.Log.Info($"[UpdateService] Downloading {update.ToolName} v{update.LatestVersion} from {update.DownloadUrl}");
                await DownloadFileAsync(update.DownloadUrl, downloadPath, progress, ct).ConfigureAwait(false);

                // 2. Verify SHA-256 (if provided)
                if (!string.IsNullOrWhiteSpace(update.Sha256))
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

                await InstallToolAsync(downloadPath, source, ct).ConfigureAwait(false);

                // 5. Regenerate manifest and clear resolver cache
                var manifestPath = Path.Combine(_appDir, ToolManifest.DefaultFileName);
                if (Directory.Exists(_toolsDir))
                {
                    await ToolManifest.WriteAsync(_toolsDir, manifestPath).ConfigureAwait(false);
                }
                ToolResolver.ClearCache();

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

    private async Task<UpdateInfo> CheckOneAsync(string toolName, ToolSource source, CancellationToken ct)
    {
        var currentVersion = GetCurrentVersion(toolName);
        var releaseUrl = $"https://api.github.com/repos/{source.GitHubOwner}/{source.GitHubRepo}/releases/latest";

        var response = await _http.GetAsync(releaseUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
                if (Regex.IsMatch(name, source.AssetPattern, RegexOptions.IgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    fileName = name;
                    break;
                }
            }
        }

        return new UpdateInfo
        {
            ToolName = toolName,
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            DownloadUrl = downloadUrl,
            Sha256 = sha256,
            ReleaseNotes = TruncateReleaseNotes(releaseNotes),
            FileName = fileName
        };
    }

    private string GetCurrentVersion(string toolName)
    {
        try
        {
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

    private async Task InstallToolAsync(string downloadPath, ToolSource source, CancellationToken ct)
    {
        if (!Directory.Exists(_toolsDir))
            Directory.CreateDirectory(_toolsDir);

        if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Remove old version directory
            if (!string.IsNullOrEmpty(source.SubDirectory))
            {
                foreach (var old in Directory.GetDirectories(_toolsDir, source.SubDirectory))
                {
                    try { Directory.Delete(old, true); }
                    catch (Exception ex) { AppLogger.Log.Debug(ex, $"[UpdateService] Failed to remove old {old}"); }
                }
            }

            // Extract zip directly into tools/
            ZipFile.ExtractToDirectory(downloadPath, _toolsDir, overwriteFiles: true);
        }
        else if (downloadPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Single executable — place in subdirectory
            if (!string.IsNullOrEmpty(source.SubDirectory))
            {
                var targetDir = Path.Combine(_toolsDir, source.SubDirectory);
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
                var targetPath = Path.Combine(targetDir, Path.GetFileName(downloadPath));
                File.Copy(downloadPath, targetPath, overwrite: true);
            }
        }
    }

    private static (bool Success, string Message) HandleSelfUpdate(string installerPath)
    {
        try
        {
            // Launch the installer and let the current process exit gracefully
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
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

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            totalRead += bytesRead;
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
        client.Timeout = TimeSpan.FromSeconds(30);
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
