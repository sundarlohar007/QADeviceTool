using System.Collections.Concurrent;
using System.IO;

namespace LogPro.Helpers;

public static class ToolResolver
{
    private static readonly string _appDir;
    private static readonly string _toolsDir;
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tool subdirectories that must NOT have their internal subfolders prepended to PATH.
    /// pymobiledevice3 is a PyInstaller bundle whose `_internal/` ships its own python313.dll,
    /// numpy DLLs, etc. — leaking those onto PATH breaks system-Python invocations.
    /// </summary>
    private static readonly string[] _pathExcludedSubdirs = { "pymobiledevice3" };
    private static bool _initialized;
    private static volatile bool _bundledToolsUntrusted;

    static ToolResolver()
    {
        _appDir = AppContext.BaseDirectory;
        _toolsDir = Path.Combine(_appDir, "tools");
    }

    public static string Resolve(string toolName)
    {
        if (_cache.TryGetValue(toolName, out var cached))
            return cached;

        var result = ResolveInternal(toolName);
        _cache[toolName] = result;
        return result;
    }

    private static string ResolveInternal(string toolName)
    {
        if (!Directory.Exists(_toolsDir))
            return toolName;
        if (_bundledToolsUntrusted)
            return Path.Combine(_toolsDir, "__untrusted_tool__");

        try
        {
            var exeName = toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? toolName
                : toolName + ".exe";

            foreach (var subDir in Directory.GetDirectories(_toolsDir))
            {
                var exePath = Path.Combine(subDir, exeName);
                if (File.Exists(exePath)) return exePath;

                var binPath = Path.Combine(subDir, "bin", exeName);
                if (File.Exists(binPath)) return binPath;
            }

            var rootExe = Path.Combine(_toolsDir, exeName);
            if (File.Exists(rootExe)) return rootExe;
        }
        catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[ToolResolver] Resolution failed"); }

        return toolName;
    }

    public static string ToolsDirectory => _toolsDir;

    public static bool BundledToolsTrusted => !_bundledToolsUntrusted;

    /// <summary>
    /// Verifies the release manifest before any bundled executable is selected. Development
    /// checkouts may omit the manifest when no bundled tools are present; a shipped bundle
    /// with tools must include it and fails closed when it is missing or modified.
    /// </summary>
    public static async Task<bool> VerifyBundledToolsAsync(bool requireManifest = true, bool requireTools = false)
    {
        if (!Directory.Exists(_toolsDir))
        {
            _bundledToolsUntrusted = requireTools;
            return !requireTools;
        }

        var manifestPath = Path.Combine(_appDir, Services.ToolManifest.DefaultFileName);
        if (!File.Exists(manifestPath))
        {
            _bundledToolsUntrusted = requireManifest && HasBundledTools;
            return !_bundledToolsUntrusted;
        }

        var result = await Services.ToolManifest.VerifyAsync(_toolsDir, manifestPath).ConfigureAwait(false);
        _bundledToolsUntrusted = !result.IsHealthy;
        return result.IsHealthy;
    }

    public static bool HasBundledTools
    {
        get
        {
            try
            {
                return Directory.Exists(_toolsDir) &&
                       Directory.GetFiles(_toolsDir, "*.exe", SearchOption.AllDirectories).Length > 0;
            }
            catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[ToolResolver] Check failed"); return false; }
        }
    }

    public static bool IsBundled(string resolvedPath)
    {
        try
        {
            return Path.IsPathRooted(resolvedPath) &&
                   resolvedPath.StartsWith(_toolsDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[ToolResolver] Check failed"); return false; }
    }

    /// <summary>
    /// Prepends safe tool subdirectories to PATH so the Windows loader can find
    /// adb.exe / scrcpy.exe / etc. and their satellite DLLs. Skips PyInstaller
    /// bundles (e.g. pymobiledevice3) because their _internal/ Python DLLs would
    /// shadow the system Python's runtime if leaked onto PATH.
    /// </summary>
    public static void InitializeNativePaths()
    {
        if (!Directory.Exists(_toolsDir)) return;
        if (_initialized) return; _initialized = true;

        try
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var newPaths = new System.Collections.Generic.List<string>();

            foreach (var subDir in Directory.GetDirectories(_toolsDir))
            {
                var subName = Path.GetFileName(subDir);
                if (_pathExcludedSubdirs.Any(ex => subName.Equals(ex, StringComparison.OrdinalIgnoreCase)))
                    continue;
                newPaths.Add(subDir);
            }

            if (newPaths.Count > 0)
            {
                var prefix = string.Join(";", newPaths) + ";";
                Environment.SetEnvironmentVariable("PATH", prefix + currentPath);
            }
        }
        catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[ToolResolver] Resolution failed"); }
    }

    /// <summary>Clears the tool resolution cache. Call after moving/deleting tools.</summary>
    public static void ClearCache()
    {
        _cache.Clear();
    }
}
