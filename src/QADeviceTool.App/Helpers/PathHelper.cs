using System.IO;

namespace QADeviceTool.Helpers;

/// <summary>
/// Utilities for PATH and directory management.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Gets the default sessions root directory under Documents.
    /// </summary>
    public static string GetDefaultSessionsDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "QA_Device_Tool", "Sessions");
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
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
