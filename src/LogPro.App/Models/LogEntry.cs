using System;
using CommunityToolkit.Mvvm.ComponentModel;

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

public partial class LogEntry : ObservableObject
{
    [ObservableProperty] private string _timestamp = string.Empty;
    [ObservableProperty] private LogLevel _level = LogLevel.Unknown;
    [ObservableProperty] private string _tag = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _rawLine = string.Empty;
    [ObservableProperty] private bool _isBookmarked;

    public override string ToString()
    {
        return $"[{Timestamp}] {Level}: {Message}";
    }
}
