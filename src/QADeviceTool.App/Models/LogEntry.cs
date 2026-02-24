using System;

namespace QADeviceTool.Models;

public enum LogLevel
{
    Verbose,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
    Unknown
}

public class LogEntry
{
    public string Timestamp { get; set; } = string.Empty;
    public LogLevel Level { get; set; } = LogLevel.Unknown;
    public string Tag { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RawLine { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"[{Timestamp}] {Level}: {Message}";
    }
}
