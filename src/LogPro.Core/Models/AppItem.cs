namespace LogPro.Models;

public enum AppCategory { Unknown, User, System, Hidden }

public class AppItem
{
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
    public AppCategory Category { get; set; } = AppCategory.Unknown;
    public bool IsRunning { get; set; }
}

public sealed record AppInventoryResult(bool Success, IReadOnlyList<AppItem> Apps, string Error = "", bool RunningStateAvailable = false)
{
    public static AppInventoryResult Failed(string error) => new(false, Array.Empty<AppItem>(), error);
}
