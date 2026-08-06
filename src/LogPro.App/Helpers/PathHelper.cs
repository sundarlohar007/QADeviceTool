using System.IO;

namespace LogPro.Helpers;

/// <summary>
/// Utilities for PATH and directory management.
/// </summary>
public static class PathHelper
{
    public const string AppDataFolderName = "LogPro";
    private const string LegacyAppDataFolderName = "QAQCDeviceTool";

    /// <summary>
    /// Root application-data directory under %LOCALAPPDATA% (single source of truth for branding).
    /// </summary>
    public static string GetAppDataDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataFolderName);

    /// <summary>
    /// One-time migration of the legacy %LOCALAPPDATA%\QAQCDeviceTool folder to LogPro
    /// (branding unification). No-op when the target already exists; returns false if it couldn't move.
    /// </summary>
    public static bool MigrateLegacyAppData(string? localAppData = null)
    {
        var root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacy = Path.Combine(root, LegacyAppDataFolderName);
        var target = Path.Combine(root, AppDataFolderName);
        if (!Directory.Exists(legacy) || Directory.Exists(target)) return true;
        try
        {
            Directory.Move(legacy, target);
            return true;
        }
        catch (Exception)
        {
            return false; // locked by another process; app keeps working, data stays in legacy folder
        }
    }

    /// <summary>
    /// Gets the default sessions root directory under Documents.
    /// </summary>
    public static string GetDefaultSessionsDirectory()
    {
        return Path.Combine(GetAppDataDirectory(), "Sessions");
    }

    /// <summary>
    /// Ensures the sessions directory exists.
    /// </summary>
    public static void EnsureSessionsDirectory()
    {
        var dir = GetDefaultSessionsDirectory();
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// Gets the path to the application configuration file.
    /// </summary>
    public static string GetConfigFilePath()
    {
        return Path.Combine(GetAppDataDirectory(), "config.txt");
    }

    /// <summary>
    /// Creates a new session directory in the format: DeviceName_HH.MM.SStt_dd.MM.yyyy
    /// Uses the provided root directory or falls back to the default.
    /// </summary>
    public static string CreateSessionDirectory(string deviceName, string? rootDirectory = null)
    {
        var safeName = SanitizeFileName(deviceName);
        var time = DateTime.Now.ToString("hh.mm.sstt");
        var date = DateTime.Now.ToString("dd.MM.yyyy");
        var dirName = $"{safeName}_{time}_{date}";
        var root = string.IsNullOrWhiteSpace(rootDirectory) ? GetDefaultSessionsDirectory() : rootDirectory;
        var fullPath = Path.Combine(root, dirName);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    /// <summary>
    /// Checks if a command is available in PATH.
    /// </summary>
    public static bool IsCommandInPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = pathEnv.Split(Path.PathSeparator);

        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path.Trim(), command);
            if (File.Exists(fullPath) || File.Exists(fullPath + ".exe"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the full path of a command in PATH.
    /// </summary>
    public static string? FindInPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = pathEnv.Split(Path.PathSeparator);

        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path.Trim(), command + ".exe");
            if (File.Exists(fullPath))
                return fullPath;

            fullPath = Path.Combine(path.Trim(), command);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }

    private static string SanitizeFileName(string name)
    {
        return SecurityHelper.SanitizeFileName(name);
    }

    /// <summary>Restricts directory access to current user (owner-only) on Windows.</summary>
    public static void RestrictDirectoryAccess(string directoryPath)
    {
        try
        {
            if (!System.IO.Directory.Exists(directoryPath)) return;
            var info = new System.IO.DirectoryInfo(directoryPath);
            var acl = info.GetAccessControl();
            // Remove inherited permissions
            acl.SetAccessRuleProtection(true, false);
            info.SetAccessControl(acl);
        }
        catch { /* ACLs best-effort on Windows */ }
    }
}

