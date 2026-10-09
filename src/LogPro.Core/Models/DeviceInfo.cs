using CommunityToolkit.Mvvm.ComponentModel;

namespace LogPro.Models;

/// <summary>
/// Represents a detected device (Android or iOS).
/// </summary>
public partial class DeviceInfo : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _id = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _serial = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _name = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _model = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _osVersion = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private DevicePlatform _platform;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private DeviceConnectionState _connectionState;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _batteryLevel = "N/A";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _batteryStatus = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _manufacturer = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _product = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _usbInfo = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _notes = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private string _tag = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private DateTime? _lastConnected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(StatusText), nameof(IsReady), nameof(MaskedSerial), nameof(DisplayNotes), nameof(PlatformIcon))]
    private bool _isTemporarilyUnavailable;

    public bool IsReady => ConnectionState == DeviceConnectionState.Online && !IsTemporarilyUnavailable;

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
