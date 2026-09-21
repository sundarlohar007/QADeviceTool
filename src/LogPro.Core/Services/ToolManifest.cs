using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LogPro.Services;

/// <summary>One bundled tool in the integrity manifest (§7.1).</summary>
public sealed class ToolManifestEntry
{
    public string Path { get; set; } = string.Empty;   // relative to the tools root
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Result of manifest verification.</summary>
public sealed class ToolManifestVerifyResult
{
    public IReadOnlyList<ToolManifestEntry> Ok { get; init; } = Array.Empty<ToolManifestEntry>();
    public IReadOnlyList<ToolManifestEntry> Mismatched { get; init; } = Array.Empty<ToolManifestEntry>();
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Unexpected { get; init; } = Array.Empty<string>();
    public bool IsHealthy => Mismatched.Count == 0 && Missing.Count == 0 && Unexpected.Count == 0;
}

/// <summary>
/// Cached verification result with file metadata for change detection.
/// </summary>
public sealed class VerificationCache
{
    public string ManifestSha256 { get; set; } = string.Empty;
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ToolManifestVerifyResult Result { get; set; } = new();
    public DateTime VerifiedAt { get; set; }
}

public sealed class FileMetadata
{
    public long Size { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
}

/// <summary>
/// sha256 integrity manifest for bundled native tools (§7.1) — written per release,
/// verified at runtime by the dependency doctor.
/// </summary>
public static class ToolManifest
{
    public const string DefaultFileName = "tools-manifest.json";
    private const string CacheFileName = "tools-manifest.cache.json";

    private static string GetCacheDirectory()
    {
        // Store cache in app data directory (outside tools) to avoid being seen as "unexpected"
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDir = Path.Combine(appData, "LogPro", "cache");
        if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
        return cacheDir;
    }

    private static string GetCachePath(string toolsRoot)
    {
        // Use tools root hash to isolate caches per installation
        var toolsHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(toolsRoot)))).ToLowerInvariant()[..16];
        return Path.Combine(GetCacheDirectory(), $"{CacheFileName}.{toolsHash}");
    }

    public static async Task WriteAsync(string toolsRoot, string manifestPath)
    {
        if (!Directory.Exists(toolsRoot)) throw new DirectoryNotFoundException(toolsRoot);
        var entries = new List<ToolManifestEntry>();
        var root = Path.GetFullPath(toolsRoot);
        var manifestFullPath = Path.GetFullPath(manifestPath);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(file), manifestFullPath, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var sha = await Sha256Async(file);
            entries.Add(new ToolManifestEntry { Path = relative, Sha256 = sha });
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var json = JsonSerializer.Serialize(entries, LogProJsonContext.Default.IReadOnlyListToolManifestEntry);
        await File.WriteAllTextAsync(manifestPath, json);

        // Invalidate cache after writing new manifest
        InvalidateCache(toolsRoot);
    }

    public static async Task<ToolManifestVerifyResult> VerifyAsync(string toolsRoot, string manifestPath)
    {
        // Try to load cached result first
        var cached = await LoadCacheAsync(toolsRoot, manifestPath);
        if (cached != null && cached.Result.IsHealthy)
        {
            LogToEarlyLog("[ToolManifest] Using cached verification result");
            return cached.Result;
        }

        LogToEarlyLog($"[ToolManifest] VerifyAsync START (cache miss or invalid) toolsRoot={toolsRoot}");
        var result = await VerifyInternalAsync(toolsRoot, manifestPath);

        // Save cache if healthy
        if (result.IsHealthy)
        {
            await SaveCacheAsync(toolsRoot, manifestPath, result);
        }

        return result;
    }

    private static async Task<ToolManifestVerifyResult> VerifyInternalAsync(string toolsRoot, string manifestPath)
    {
        if (!Directory.Exists(toolsRoot))
            return new ToolManifestVerifyResult { Missing = new[] { "tools directory" } };

        if (!File.Exists(manifestPath))
            return new ToolManifestVerifyResult { Missing = new[] { Path.GetFileName(manifestPath) } };

        string json;
        try { json = File.ReadAllText(manifestPath); }
        catch (Exception ex)
        {
            return new ToolManifestVerifyResult { Mismatched = new[] { new ToolManifestEntry { Path = $"manifest unreadable: {ex.GetType().Name}" } } };
        }

        IReadOnlyList<ToolManifestEntry>? manifest;
        try { manifest = JsonSerializer.Deserialize(json, LogProJsonContext.Default.IReadOnlyListToolManifestEntry); }
        catch (JsonException) { manifest = null; }

        if (manifest == null)
            return new ToolManifestVerifyResult { Mismatched = new ToolManifestEntry[] { new() { Path = "manifest unreadable" } } };

        var root = Path.GetFullPath(toolsRoot);
        var ok = new List<ToolManifestEntry>();
        var mismatched = new List<ToolManifestEntry>();
        var missing = new List<string>();
        var manifestPaths = manifest.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);

        if (manifest.Count != manifestPaths.Count)
            return new ToolManifestVerifyResult { Mismatched = new[] { new ToolManifestEntry { Path = "manifest contains duplicate paths" } } };

        foreach (var entry in manifest)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) ||
                !Regex.IsMatch(entry.Sha256 ?? string.Empty, "^[0-9a-fA-F]{64}$") ||
                Path.IsPathRooted(entry.Path))
            {
                mismatched.Add(entry);
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                mismatched.Add(entry);
                continue;
            }
            if (!File.Exists(full)) { missing.Add(entry.Path); continue; }
            var sha = await Sha256Async(full);
            if (sha.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) ok.Add(entry);
            else mismatched.Add(entry);
        }

        var unexpected = new List<string>();
        var manifestFullPath = Path.GetFullPath(manifestPath);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(file), manifestFullPath, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!manifestPaths.Contains(relative)) unexpected.Add(relative);
        }

        return new ToolManifestVerifyResult
        {
            Ok = ok,
            Mismatched = mismatched,
            Missing = missing,
            Unexpected = unexpected
        };
    }

    private static async Task<VerificationCache?> LoadCacheAsync(string toolsRoot, string manifestPath)
    {
        var cachePath = GetCachePath(toolsRoot);
        if (!File.Exists(cachePath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(cachePath);
            var cache = JsonSerializer.Deserialize(json, LogProJsonContext.Default.VerificationCache);
            if (cache == null) return null;

            // Validate cache: check manifest hash and file metadata
            var manifestHash = await ComputeFileSha256Async(manifestPath);
            if (!string.Equals(cache.ManifestSha256, manifestHash, StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var kvp in cache.FileMetadata)
            {
                var fullPath = Path.Combine(toolsRoot, kvp.Key);
                if (!File.Exists(fullPath)) return null;
                var fi = new FileInfo(fullPath);
                if (fi.Length != kvp.Value.Size || fi.LastWriteTimeUtc != kvp.Value.LastWriteTimeUtc)
                    return null;
            }

            return cache;
        }
        catch { return null; }
    }

    private static async Task SaveCacheAsync(string toolsRoot, string manifestPath, ToolManifestVerifyResult result)
    {
        try
        {
            var cache = new VerificationCache
            {
                ManifestSha256 = await ComputeFileSha256Async(manifestPath),
                FileMetadata = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase),
                Result = result,
                VerifiedAt = DateTime.UtcNow
            };

            foreach (var entry in result.Ok)
            {
                var fullPath = Path.Combine(toolsRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    var fi = new FileInfo(fullPath);
                    cache.FileMetadata[entry.Path] = new FileMetadata
                    {
                        Size = fi.Length,
                        LastWriteTimeUtc = fi.LastWriteTimeUtc
                    };
                }
            }

            var cachePath = GetCachePath(toolsRoot);
            var json = JsonSerializer.Serialize(cache, LogProJsonContext.Default.VerificationCache);
            await File.WriteAllTextAsync(cachePath, json);
        }
        catch { }
    }

    private static void InvalidateCache(string toolsRoot)
    {
        try
        {
            var cachePath = GetCachePath(toolsRoot);
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch { }
    }

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length == 0)
        {
            return "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        }
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void LogToEarlyLog(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "LogPro_startup-debug.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }
}
