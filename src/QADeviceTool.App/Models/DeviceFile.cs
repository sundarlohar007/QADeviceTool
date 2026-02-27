using System.IO;

namespace QADeviceTool.Models;

/// <summary>
/// Represents a file or directory on a connected device.
/// </summary>
public class DeviceFile
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedDate { get; set; }

    /// <summary>
    /// Formats the file size into a human-readable string (KB, MB, GB).
    /// </summary>
    public string DisplaySize
    {
        get
        {
            if (IsDirectory) return string.Empty;
            if (Size < 1024) return $"{Size} B";
            if (Size < 1024 * 1024) return $"{Size / 1024.0:F1} KB";
            if (Size < 1024 * 1024 * 1024) return $"{Size / (1024.0 * 1024.0):F2} MB";
            return $"{Size / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    /// <summary>
    /// Material Design icon code for the UI based on file type.
    /// </summary>
    public string Icon => IsDirectory ? "\uE2C7" : "\uE873"; // Folder vs InsertDriveFile

    /// <summary>
    /// Formatted modified date for the UI list view.
    /// </summary>
    public string DisplayDate => ModifiedDate != DateTime.MinValue 
        ? ModifiedDate.ToString("yyyy-MM-dd HH:mm") 
        : string.Empty;
}
