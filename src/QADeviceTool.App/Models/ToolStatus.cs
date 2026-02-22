namespace QADeviceTool.Models;

/// <summary>
/// Represents the availability status of an external tool dependency.
/// </summary>
public class ToolStatus
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsInstalled { get; set; }
    public string Version { get; set; } = "Not found";
    public string Path { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;

    public string StatusIcon => IsInstalled ? "[OK]" : "[X]";
    public string StatusColor => IsInstalled ? "Green" : "Red";
}
