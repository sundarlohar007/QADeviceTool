using System.IO;

namespace QADeviceTool.Helpers;

/// <summary>
/// Resolves tool executables by checking the bundled tools/ directory first,
/// then falling back to system PATH. This allows shipping scrcpy, ADB,
/// and libimobiledevice inside the app without requiring system-wide install.
///
/// Expected tools/ layout:
///   tools/scrcpy-win64-v3.3.4/   → scrcpy.exe, adb.exe
///   tools/iMobileDevice/          → idevice_id.exe, ideviceinfo.exe, etc.
/// </summary>
public static class ToolResolver
{
    private static readonly string _appDir;
    private static readonly string _toolsDir;

    // Known subfolder names to search (order matters — first match wins)
    private static readonly string[][] _searchFolders = new[]
    {
        // ADB: scrcpy bundle ships its own adb.exe, also check platform-tools
        new[] { "scrcpy-win64-*", "platform-tools", "platform-tools-*" },
        // scrcpy
        new[] { "scrcpy-win64-*", "scrcpy-*", "scrcpy" },
        // libimobiledevice tools
        new[] { "iMobileDevice", "libimobiledevice-*", "libimobiledevice" }
    };

    static ToolResolver()
    {
        _appDir = AppContext.BaseDirectory;
        _toolsDir = Path.Combine(_appDir, "tools");
    }

    /// <summary>
    /// Finds the full path to a tool executable.
    /// Search order:
    ///   1. All subdirectories matching known patterns in tools/
    ///   2. tools/ root
    ///   3. System PATH (bare command name)
    /// </summary>
    public static string Resolve(string toolName)
    {
        if (!Directory.Exists(_toolsDir))
            return toolName;

        // 1. Search all subdirectories in tools/ for the executable
        try
        {
            foreach (var subDir in Directory.GetDirectories(_toolsDir))
            {
                var exePath = Path.Combine(subDir, toolName + ".exe");
                if (File.Exists(exePath)) return exePath;

                // Check one level deeper (e.g. tools/folder/bin/tool.exe)
                var binPath = Path.Combine(subDir, "bin", toolName + ".exe");
                if (File.Exists(binPath)) return binPath;
            }
        }
        catch { }

        // 2. Check tools/ root
        var rootExe = Path.Combine(_toolsDir, toolName + ".exe");
        if (File.Exists(rootExe)) return rootExe;

        // 3. Fall back to bare command name (uses system PATH)
        return toolName;
    }

    /// <summary>
    /// Gets the tools directory path.
    /// </summary>
    public static string ToolsDirectory => _toolsDir;

    /// <summary>
    /// Checks if tools directory exists and has content.
    /// </summary>
    public static bool HasBundledTools
    {
        get
        {
            try
            {
                return Directory.Exists(_toolsDir) &&
                       Directory.GetFiles(_toolsDir, "*.exe", SearchOption.AllDirectories).Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Checks whether the resolved path points to a bundled tool (inside tools/ dir)
    /// vs a system PATH command.
    /// </summary>
    public static bool IsBundled(string resolvedPath)
    {
        try
        {
            return Path.IsPathRooted(resolvedPath) &&
                   resolvedPath.StartsWith(_toolsDir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
