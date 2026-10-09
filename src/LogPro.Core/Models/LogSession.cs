using CommunityToolkit.Mvvm.ComponentModel;

namespace LogPro.Models;

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
    public string DeviceSerial { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
    public string LogFilePath { get; set; } = string.Empty;
    public string AppLogFilePath { get; set; } = string.Empty;
    public string SessionDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private SessionStatus _status = SessionStatus.Idle;

    [ObservableProperty]
    private string _captureError = string.Empty;

    public string CaptureNotice { get; set; } = string.Empty;

    public long StopRevision;
    public string StopReason { get; set; } = "";
    public int? ExitCode { get; set; }
    public bool CaptureComplete { get; set; }
    public DateTime? LastLogUtc { get; set; }
    public int InterruptionCount { get; set; }
    public DateTime? LastInterruptedUtc { get; set; }
    public DateTime? LastResumedUtc { get; set; }
    public LogcatBuffer Buffer { get; set; } = LogcatBuffer.Main;
    public LogcatFormat Format { get; set; } = LogcatFormat.ThreadTime;
    public string? TargetPackage { get; set; }
    public LogPro.Services.CrashDetector Crashes { get; } = new();
    public void RefreshDuration() { OnPropertyChanged(nameof(DurationText)); OnPropertyChanged(nameof(LogLineCount)); }

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

public sealed record CaptureOptions(LogcatBuffer Buffer, LogcatFormat Format, string TargetPackage);
