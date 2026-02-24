using CommunityToolkit.Mvvm.ComponentModel;

namespace QADeviceTool.Models;

/// <summary>
/// Represents a log capture session.
/// </summary>
public partial class LogSession : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime? EndTime { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
    public string LogFilePath { get; set; } = string.Empty;
    public string SessionDirectory { get; set; } = string.Empty;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private SessionStatus _status = SessionStatus.Idle;
    
    public long LogLineCount { get; set; }

    public string DurationText
    {
        get
        {
            var end = EndTime ?? DateTime.Now;
            var duration = end - StartTime;
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                : $"{duration.Minutes}m {duration.Seconds}s";
        }
    }

    public string StatusIcon => Status switch
    {
        SessionStatus.Capturing => "[REC]",
        SessionStatus.Stopped => "[STOP]",
        SessionStatus.Idle => "[IDLE]",
        _ => "[?]"
    };
}

public enum SessionStatus
{
    Idle,
    Capturing,
    Stopped
}
