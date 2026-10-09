using System.Text.RegularExpressions;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>Shared structured parsing for live display and exported records. Continuation/raw lines have unknown severity.</summary>
public static class LogLineParser
{
    public static LogEntry Parse(string rawLine, bool rawMode = false)
    {
        var entry = new LogEntry { RawLine = rawLine, Message = rawLine, Level = LogLevel.Unknown };
        entry.Level = DetectLogLevel(rawLine);

        if (!rawMode)
        {
            try
            {
                var android = _logcatStructuredRx.Match(rawLine);
                if (!android.Success) android = _otherAndroidRx.Match(rawLine);
                if (android.Success)
                {
                    entry.Timestamp = android.Groups["timestamp"].Value;
                    entry.Tag = android.Groups["tag"].Value.Trim();
                    entry.Message = android.Groups["message"].Value;
                }
                else if (rawLine.StartsWith("["))
                {
                    int closeBracket = rawLine.IndexOf(']');
                    if (closeBracket > 1)
                    {
                        entry.Timestamp = rawLine.Substring(1, closeBracket - 1);
                        entry.Message = rawLine.Substring(closeBracket + 1).TrimStart();
                    }
                }
            }
            catch (Exception ex) { Services.AppLogger.Log.Debug(ex, "[SessionViewModel] Parse failed, keeping raw message"); }
        }
        return entry;
    }

    private static readonly Regex _logcatStructuredRx = new(
        @"^(?<timestamp>\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s+\d+\s+\d+\s+[VDIWEFA]\s+(?<tag>[^:]+):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    // Android threadtime format: "MM-DD HH:MM:SS.mmm  PID  TID L Tag: msg"
    //   level letter sits between TID and Tag, separated by single spaces.
    private static readonly System.Text.RegularExpressions.Regex _logcatThreadtimeRx =
        new(@"^\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+\d+\s+\d+\s+([VDIWEFA])\s",
            System.Text.RegularExpressions.RegexOptions.Compiled | RegexOptions.NonBacktracking);

    // Android brief/tag format: "L/Tag(pid): msg"  — level letter at index 0, slash at index 1.
    private static readonly System.Text.RegularExpressions.Regex _logcatBriefRx =
        new(@"^([VDIWEFA])/[A-Za-z0-9_\.\-]+",
            System.Text.RegularExpressions.RegexOptions.Compiled | RegexOptions.NonBacktracking);

    // iOS syslog (pymobiledevice3) emits Apple os_log style:
    //   "<TS> <host> <process>[<pid>] <<Level>>: msg"   — level inside angle brackets
    //   "<TS> ... <Level>: msg"                         — bare bracket-less level token
    private static readonly System.Text.RegularExpressions.Regex _iosSyslogAngleRx =
        new(@"<(Default|Info|Notice|Debug|Error|Fault|Warning)>",
            System.Text.RegularExpressions.RegexOptions.Compiled | RegexOptions.NonBacktracking |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Bracketed leading level: "[ERROR] msg" / "[E] msg".
    private static readonly System.Text.RegularExpressions.Regex _bracketedLevelRx =
        new(@"^\s*\[(?<lvl>FATAL|FTL|ERROR|ERR|WARNING|WARN|INFO|DEBUG|DBG|TRACE|VERBOSE|VRB|F|E|W|I|D|V)\]",
            System.Text.RegularExpressions.RegexOptions.Compiled | RegexOptions.NonBacktracking |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly Regex _otherAndroidRx = new(
        @"^(?:(?<timestamp>\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+)\s+)?(?<level>[VDIWEFA])(?:/(?<tag>[^(:]+)(?:\(\s*\d+\))?|\(\s*\d+(?::\s*\d+)?\)):\s*(?<message>.*)$|^\[\s*(?<timestamp>\d{2}-\d{2}\s+\S+)\s+\d+:\s*\d+\s+(?<level>[VDIWEFA])/(?<tag>[^\]]+)\]$",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    public static LogLevel DetectLogLevel(string rawLine)
    {
        if (string.IsNullOrEmpty(rawLine))
            return LogLevel.Unknown;

        var trimmed = rawLine.TrimStart();

        // 1. Android logcat threadtime — most common live-capture format.
        var m = _logcatThreadtimeRx.Match(trimmed);
        if (m.Success) return LetterToLevel(m.Groups[1].Value[0]);

        // 2. Android logcat brief/tag — "E/MyTag(123): msg".
        m = _logcatBriefRx.Match(trimmed);
        if (m.Success) return LetterToLevel(m.Groups[1].Value[0]);

        m = _otherAndroidRx.Match(trimmed);
        if (m.Success) return LetterToLevel(m.Groups["level"].Value[0]);

        // 3. iOS syslog with <Level> tag.
        m = _iosSyslogAngleRx.Match(trimmed);
        if (m.Success) return AppleOsLogToLevel(m.Groups[1].Value);

        // 4. Bracketed leading level.
        m = _bracketedLevelRx.Match(trimmed);
        if (m.Success) return TokenToLevel(m.Groups["lvl"].Value);

        // 5. Anchored token at line start (avoid scanning the whole payload — that
        //    misclassifies messages that merely *contain* the word "info" / "error").
        var prefix = trimmed.Length > 16 ? trimmed.Substring(0, 16).ToUpperInvariant() : trimmed.ToUpperInvariant();
        if (StartsWithToken(prefix, "FATAL") || StartsWithToken(prefix, "FTL")) return LogLevel.Fatal;
        if (StartsWithToken(prefix, "ERROR") || StartsWithToken(prefix, "ERR")) return LogLevel.Error;
        if (StartsWithToken(prefix, "WARNING") || StartsWithToken(prefix, "WARN")) return LogLevel.Warning;
        if (StartsWithToken(prefix, "INFO")) return LogLevel.Info;
        if (StartsWithToken(prefix, "DEBUG") || StartsWithToken(prefix, "DBG")) return LogLevel.Debug;
        if (StartsWithToken(prefix, "TRACE") || StartsWithToken(prefix, "VERBOSE") || StartsWithToken(prefix, "VRB")) return LogLevel.Verbose;

        return LogLevel.Unknown;
    }

    private static LogLevel LetterToLevel(char c) => c switch
    {
        'F' or 'A' => LogLevel.Fatal, // 'A' = Assert in some Android logcat builds
        'E' => LogLevel.Error,
        'W' => LogLevel.Warning,
        'I' => LogLevel.Info,
        'D' => LogLevel.Debug,
        'V' => LogLevel.Verbose,
        _ => LogLevel.Unknown
    };

    private static LogLevel AppleOsLogToLevel(string token) => token.ToUpperInvariant() switch
    {
        "FAULT" => LogLevel.Fatal,
        "ERROR" => LogLevel.Error,
        "WARNING" => LogLevel.Warning,
        "NOTICE" or "DEFAULT" or "INFO" => LogLevel.Info,
        "DEBUG" => LogLevel.Debug,
        _ => LogLevel.Unknown
    };

    private static LogLevel TokenToLevel(string token) => token.ToUpperInvariant() switch
    {
        "FATAL" or "FTL" or "F" => LogLevel.Fatal,
        "ERROR" or "ERR" or "E" => LogLevel.Error,
        "WARNING" or "WARN" or "W" => LogLevel.Warning,
        "INFO" or "I" => LogLevel.Info,
        "DEBUG" or "DBG" or "D" => LogLevel.Debug,
        "TRACE" or "VERBOSE" or "VRB" or "V" => LogLevel.Verbose,
        _ => LogLevel.Unknown
    };

    private static bool StartsWithToken(string upper, string token)
    {
        if (!upper.StartsWith(token)) return false;
        // ensure it's a token boundary (next char is non-alpha or end of string)
        if (upper.Length == token.Length) return true;
        var next = upper[token.Length];
        return !(char.IsLetter(next) || next == '_');
    }

}
