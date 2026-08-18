using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public bool IsHealthy => Mismatched.Count == 0 && Missing.Count == 0;
}

/// <summary>
/// sha256 integrity manifest for bundled native tools (§7.1) — written per release,
/// verified at runtime by the dependency doctor.
/// </summary>
public static class ToolManifest
{
    public const string DefaultFileName = "tools-manifest.json";

    public static async Task WriteAsync(string toolsRoot, string manifestPath)
    {
        var entries = new List<ToolManifestEntry>();
        var root = Path.GetFullPath(toolsRoot);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var sha = await Sha256Async(file);
            entries.Add(new ToolManifestEntry { Path = relative, Sha256 = sha });
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var json = JsonSerializer.Serialize(entries, LogProJsonContext.Default.IReadOnlyListToolManifestEntry);
        await File.WriteAllTextAsync(manifestPath, json);
    }

    public static async Task<ToolManifestVerifyResult> VerifyAsync(string toolsRoot, string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return new ToolManifestVerifyResult { Missing = new[] { Path.GetFileName(manifestPath) } };
        }

        var json = await File.ReadAllTextAsync(manifestPath);
        IReadOnlyList<ToolManifestEntry>? manifest;
        try { manifest = JsonSerializer.Deserialize(json, LogProJsonContext.Default.IReadOnlyListToolManifestEntry); }
        catch (JsonException) { manifest = null; }

        if (manifest == null)
        {
            return new ToolManifestVerifyResult { Mismatched = new ToolManifestEntry[] { new() { Path = "manifest unreadable" } } };
        }

        var root = Path.GetFullPath(toolsRoot);
        var ok = new List<ToolManifestEntry>();
        var mismatched = new List<ToolManifestEntry>();
        var missing = new List<string>();
        var manifestPaths = manifest.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var actualPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in manifest)
        {
            var full = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) { missing.Add(entry.Path); continue; }
            actualPaths.Add(entry.Path);
            var sha = await Sha256Async(full);
            if (sha.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) ok.Add(entry);
            else mismatched.Add(entry);
        }

        var unexpected = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
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

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
