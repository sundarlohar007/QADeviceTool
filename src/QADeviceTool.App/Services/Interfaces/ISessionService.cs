using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Interface for log capture session management.
/// </summary>
public interface ISessionService
{


    string SessionsRootDirectory { get; set; }

    LogSession CreateSession(DeviceInfo device, string? customSessionName = null);
    bool StartCapture(LogSession session);
    void StopCapture(LogSession session);
    void StopAllCaptures();
    LogSession? StopCaptureForDevice(string deviceSerial, IEnumerable<LogSession> sessions);
    Task<string> ReadLogContentAsync(LogSession session, int maxLines = 200000);
    Task<string> SaveLogToFileAsync(LogSession session, string logContent);
    List<LogSession> GetSavedSessions();
    bool DeleteSession(LogSession session);
    LogSession? GetActiveSessionForDevice(string deviceSerial);
    Task<bool> ExportToCsvAsync(LogSession session, string outputPath, bool anonymize = false);
    Task<bool> ExportToJsonAsync(LogSession session, string outputPath, bool anonymize = false);
}
