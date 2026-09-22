namespace LogPro.Models;

/// <summary>
/// Describes a single updatable tool or the application itself.
/// </summary>
public sealed class UpdateInfo
{
    public string ToolName { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public bool IsNewerAvailable => !string.IsNullOrEmpty(LatestVersion) &&
                                     !string.Equals(CurrentVersion, LatestVersion, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Preferences for the auto-update subsystem.</summary>
public sealed class UpdatePreferences
{
    public bool CheckOnStartup { get; set; } = true;
    public int CheckIntervalHours { get; set; } = 24;
    public DateTime LastCheckUtc { get; set; } = DateTime.MinValue;
    public List<string> SuppressedVersions { get; set; } = new(); // e.g. "scrcpy:3.4.0"
}
