using System;

namespace LogPro.Models;

public enum LogLevel
{
    Unknown = 0,
    Verbose,
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

public enum LogcatBuffer
{
    Main,
    System,
    Events,
    Crash,
    Radio
}

public enum LogcatFormat
{
    Brief,
    Process,
    Tag,
    Thread,
    Time,
    ThreadTime,
    Long,
    Raw
}

public class LogEntry
{
    public string Timestamp { get; set; } = string.Empty;
    public LogLevel Level { get; set; } = LogLevel.Unknown;
    public string Tag { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RawLine { get; set; } = string.Empty;
    public bool IsBookmarked { get; set; }

    public override string ToString()
    {
        return $"[{Timestamp}] {Level}: {Message}";
    }
}
