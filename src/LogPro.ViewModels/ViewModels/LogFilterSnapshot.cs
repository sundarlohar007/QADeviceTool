using LogPro.Models;
using System.Text.RegularExpressions;

namespace LogPro.ViewModels;

internal sealed class LogFilterSnapshot(IEnumerable<LogLevel> levels, string search, bool useRegex, bool bookmarksOnly)
{
    private readonly HashSet<LogLevel> _levels = levels.ToHashSet();
    private readonly Regex? _regex = CreateRegex(search, useRegex);
    private int _timedOut;
    public string? Error => Volatile.Read(ref _timedOut) != 0 ? "Regex search timed out. Use a simpler expression."
        : useRegex && !string.IsNullOrWhiteSpace(search) && _regex == null ? "Invalid regex expression." : null;
    private static Regex? CreateRegex(string search, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(search)) return null;
        try { return new Regex(search, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(25)); }
        catch (ArgumentException) { return null; }
    }
    public bool Matches(LogEntry entry, bool bookmarked)
    {
        if (!_levels.Contains(entry.Level) || bookmarksOnly && !bookmarked) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;
        if (!useRegex) return entry.RawLine.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) || entry.Tag.Contains(search, StringComparison.OrdinalIgnoreCase);
        if (_regex == null || Volatile.Read(ref _timedOut) != 0) return false;
        try { return _regex.IsMatch(entry.RawLine); }
        catch (RegexMatchTimeoutException) { Interlocked.Exchange(ref _timedOut, 1); return false; }
    }
}
