namespace QADeviceTool.Models;

/// <summary>
/// Represents a detected device (Android or iOS).
/// </summary>
public class DeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
    public DeviceConnectionState ConnectionState { get; set; }
    public string BatteryLevel { get; set; } = "N/A";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Model : Name;
    public string PlatformIcon => Platform == DevicePlatform.Android ? "Android" : "iOS";
    public string StatusText => ConnectionState switch
    {
        DeviceConnectionState.Online => "Connected",
        DeviceConnectionState.Unauthorized => "Unauthorized (Accept RSA)",
        DeviceConnectionState.PendingTrust => "Trust Dialog Pending",
        _ => "Offline"
    };
}

public enum DevicePlatform
{
    Android,
    iOS
}

public enum DeviceConnectionState
{
    Online,
    Offline,
    Unauthorized,
    PendingTrust
}
