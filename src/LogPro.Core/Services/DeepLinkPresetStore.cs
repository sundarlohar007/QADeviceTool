using System.Text.Json;
using LogPro.Helpers;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>Only explicitly saved presets persist. History and device identifiers are never stored here.</summary>
public sealed class DeepLinkPresetStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(PathHelper.GetAppDataDirectory(), "DeepLinks", "presets.json");
    public const int MaxPresets = 50;
    public IReadOnlyList<DeepLinkPreset> Load(out string error)
    {
        error = string.Empty;
        try
        {
            if (!File.Exists(_path)) return Array.Empty<DeepLinkPreset>();
            if (!PathHelper.IsSafeLocalPath(_path) || new FileInfo(_path).Length > 3_000_000) throw new IOException();
            var presets = JsonSerializer.Deserialize<List<DeepLinkPreset>>(File.ReadAllText(_path)) ?? new();
            if (presets.Count > MaxPresets || presets.Any(p => !IsValid(p))) throw new IOException();
            return presets;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { error = "Saved presets could not be loaded. The file is invalid or inaccessible."; return Array.Empty<DeepLinkPreset>(); }
    }
    public static bool IsValid(DeepLinkPreset? p) => p != null && !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length <= 80 &&
        !p.Name.Any(char.IsControl) && DeepLinkHelper.TryValidate(p.Uri, out _) && p.PackageId != null &&
        (p.PackageId.Length == 0 || DeepLinkHelper.IsPackageId(p.PackageId)) && DeepLinkHelper.TryValidateOptions(p.Uri, new(p.PackageId, p.Browsable), out _);
    public bool Save(IEnumerable<DeepLinkPreset> presets, out string error)
    {
        error = string.Empty;
        string? temporary = null;
        try
        {
            var list = presets.ToList();
            if (list.Count > MaxPresets || list.Any(p => !IsValid(p)) || !PathHelper.IsSafeLocalPath(_path)) throw new IOException();
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            if (!PathHelper.RestrictDirectoryAccess(directory)) throw new IOException();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(list));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { error = "Presets could not be saved securely. They remain available in memory."; return false; }
        finally
        {
            if (temporary != null && File.Exists(temporary))
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public bool Delete(out string error)
    {
        error = string.Empty;
        try { if (!PathHelper.IsSafeLocalPath(_path)) throw new IOException(); File.Delete(_path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { error = "The saved preset file could not be removed. Retry before assuming it has been deleted."; return false; }
    }
}
