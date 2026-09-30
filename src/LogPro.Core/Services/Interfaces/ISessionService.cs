using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Interface for log capture session management.
/// </summary>
public interface ISessionService
{


    event Action<string, string>? LogBatchReceived;
    event Action<LogSession>? CaptureStarted;
    event Action<LogSession>? CaptureStopped;

    IReadOnlyList<LogSession> ActiveSessions { get; }
    CaptureStatistics GetCaptureStatistics(string sessionId);

    string SessionsRootDirectory { get; set; }

    LogSession CreateSession(DeviceInfo device, string? customSessionName = null);
    Task<bool> StartCaptureAsync(LogSession session, LogcatBuffer buffer = LogcatBuffer.Main, LogcatFormat format = LogcatFormat.ThreadTime);
    void StopCapture(LogSession session);
    Task WaitForCaptureStopAsync(LogSession session) => Task.CompletedTask;
    void StopAllCaptures();
    LogSession? StopCaptureForDevice(string deviceSerial, IEnumerable<LogSession> sessions);
    Task<string> ReadLogContentAsync(LogSession session, int maxLines = 200000);
    Task<string> SaveLogToFileAsync(LogSession session, string logContent);
    Task<string> SaveLogCopyAsync(LogSession session);
    List<LogSession> GetSavedSessions();
    bool DeleteSession(LogSession session);
    LogSession? GetActiveSessionForDevice(string deviceSerial);
    Task<bool> ExportToCsvAsync(LogSession session, string outputPath, bool anonymize = true);
    Task<bool> ExportToJsonAsync(LogSession session, string outputPath, bool anonymize = true);
}
