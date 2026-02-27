namespace QADeviceTool.Models;

public class AppItem
{
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
}
