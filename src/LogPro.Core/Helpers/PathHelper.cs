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
        var requestedRoot = string.IsNullOrWhiteSpace(rootDirectory) ? GetDefaultSessionsDirectory() : rootDirectory;
        if (!TryGetSafeLocalDirectory(requestedRoot, out var root))
            throw new ArgumentException("Session output must be on a local, non-reparse-point volume.", nameof(rootDirectory));
        var fullPath = Path.Combine(root, dirName);
        Directory.CreateDirectory(fullPath);
        RestrictDirectoryAccess(fullPath); // SEC-13: owner-only on Windows
        return fullPath;
    }

    /// <summary>
    /// Rejects UNC/network/removable paths and existing reparse points. This is used for
    /// capture/export destinations because a configured output path is an automatic data
    /// transfer boundary in an offline QA environment.
    /// </summary>
    public static bool IsSafeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            if (fullPath.StartsWith("\\\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
                return false;

            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root)) return false;

            if (OperatingSystem.IsWindows())
            {
                if (root.StartsWith("\\\\", StringComparison.Ordinal)) return false;
                var driveType = new DriveInfo(root).DriveType;
                if (driveType != DriveType.Fixed) return false;
            }

            return !ContainsReparsePoint(fullPath);
        }
        catch { return false; }
    }

    public static bool TryGetSafeLocalDirectory(string? path, out string directory)
    {
        directory = string.Empty;
        if (!IsSafeLocalPath(path)) return false;

        try
        {
            directory = Path.GetFullPath(path!.Trim());
            Directory.CreateDirectory(directory);
            return IsSafeLocalPath(directory);
        }
        catch { directory = string.Empty; return false; }
    }

    private static bool ContainsReparsePoint(string fullPath)
    {
        try
        {
            if ((File.Exists(fullPath) || Directory.Exists(fullPath)) &&
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        catch (UnauthorizedAccessException) { return true; }

        var current = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { return true; }

            var parent = Directory.GetParent(current);
            if (parent == null || string.Equals(parent.FullName, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent.FullName;
        }

        return false;
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
    public static bool RestrictDirectoryAccess(string directoryPath)
    {
        if (!OperatingSystem.IsWindows()) return true; // ACL API is Windows-only (SEC-13)
        try
        {
            if (!System.IO.Directory.Exists(directoryPath)) return false;
            var info = new System.IO.DirectoryInfo(directoryPath);
            var acl = info.GetAccessControl();
            // Remove inherited permissions, then grant owner-only access (strip-without-add = deny-all).
            acl.SetAccessRuleProtection(true, false);
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (user != null)
            {
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    user,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                    System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            }
            info.SetAccessControl(acl);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

