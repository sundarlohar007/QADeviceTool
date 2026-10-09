using LogPro.Models;
using System.Text.RegularExpressions;

namespace LogPro.Services;

/// <summary>One parser per ordered stream. Long-format continuations inherit their record header.</summary>
public sealed class LogStreamParser(LogcatFormat format)
{
    private LogEntry? _header;
    private static readonly Regex LongHeader = new(@"^\[\s*\d{2}-\d{2}\s+\S+\s+\d+:\s*\d+\s+[VDIWEFA]/[^\]]+\]$", RegexOptions.NonBacktracking);
    public LogEntry Parse(string line)
    {
        if (format == LogcatFormat.Raw) return new LogEntry { RawLine = line, Message = line };
        var entry = LogLineParser.Parse(line);
        if (format != LogcatFormat.Long) return entry;
        if (LongHeader.IsMatch(line)) { _header = entry; return entry; }
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---")) { _header = null; return entry; }
        if (_header != null)
        { entry.Timestamp = _header.Timestamp; entry.Tag = _header.Tag; entry.Level = _header.Level; entry.Message = line; }
        return entry;
    }
}
