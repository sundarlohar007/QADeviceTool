namespace LogPro.Models;

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
    public string BatteryStatus { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string UsbInfo { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public DateTime? LastConnected { get; set; }
    public bool IsTemporarilyUnavailable { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Model : Name;
    public string MaskedSerial => Serial.Length <= 4 ? "••••" : $"••••{Serial[^4..]}";
    public string DisplayNotes => string.IsNullOrEmpty(Notes) ? "No notes" : Notes;
    public string PlatformIcon => Platform == DevicePlatform.Android ? "Android" : "iOS";
    public string StatusText => IsTemporarilyUnavailable ? "Reconnecting (device missed recent checks)" : ConnectionState switch
    {
        DeviceConnectionState.Online => "Connected",
        DeviceConnectionState.Unauthorized => "Unauthorized (Accept RSA)",
        DeviceConnectionState.PendingTrust => "Trust Dialog Pending",
        _ => "Offline"
    };

    public DeviceInfo WithTemporaryUnavailable(bool unavailable) => new()
    {
        Id = Id,
        Serial = Serial,
        Name = Name,
        Model = Model,
        OsVersion = OsVersion,
        Platform = Platform,
        ConnectionState = ConnectionState,
        BatteryLevel = BatteryLevel,
        BatteryStatus = BatteryStatus,
        Manufacturer = Manufacturer,
        Product = Product,
        UsbInfo = UsbInfo,
        Notes = Notes,
        Tag = Tag,
        LastConnected = LastConnected,
        IsTemporarilyUnavailable = unavailable
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
