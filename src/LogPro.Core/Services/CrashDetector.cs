using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// Detects crashes, ANRs, and fatal errors in device log streams.
/// Android: logcat patterns. iOS: syslog patterns.
/// </summary>
public class CrashDetector
{
    private readonly object _crashLock = new();
    // Android crash patterns
    private static readonly Regex[] AndroidCrashPatterns =
    {
        new(@"FATAL EXCEPTION:", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"AndroidRuntime:\s*FATAL", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bANR\s+in\b", RegexOptions.Compiled),
        new(@"Process\s+.*\s+has\s+died", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\*\*\*\s+\*\*\*\s+\*\*\*\s+\*\*\*\s+\*\*\*\s+\*\*\*", RegexOptions.Compiled), // native tombstone
        new(@"^DEBUG\s+\*\*\*", RegexOptions.Compiled), // native crash signal
        new(@"BEGIN\s+CRASH", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"force\s+finishing\s+activity", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    // iOS crash patterns
    private static readonly Regex[] IosCrashPatterns =
    {
        new(@"Exception\s+Type:", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Crashed\s+Thread:", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Termination\s+Reason:", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Application\s+.*\s+terminated", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Segmentation\s+fault", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Bus\s+error", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Abort\s+trap", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Fatal\s+error", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    /// <summary>
    /// Detected crash event with details.
    /// </summary>
    public class CrashEvent
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string Pattern { get; init; } = string.Empty;
        public string Line { get; init; } = string.Empty;
        public DevicePlatform Platform { get; init; }
        public int LineIndex { get; init; }
    }

    private readonly List<CrashEvent> _detectedCrashes = new();
    public IReadOnlyList<CrashEvent> DetectedCrashes { get { lock (_crashLock) return _detectedCrashes.ToList(); } }
    public int CrashCount { get { lock (_crashLock) return _detectedCrashes.Count; } }

    public event Action<CrashEvent>? CrashDetected;

    /// <summary>
    /// Scan a single log line for crash patterns.
    /// Returns CrashEvent if detected, null otherwise.
    /// </summary>
    public CrashEvent? ScanLine(string rawLine, int lineIndex, DevicePlatform platform)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return null;

        var patterns = platform == DevicePlatform.Android ? AndroidCrashPatterns : IosCrashPatterns;

        foreach (var regex in patterns)
        {
            if (regex.IsMatch(rawLine))
            {
                var crash = new CrashEvent
                {
                    Timestamp = DateTime.Now,
                    Pattern = regex.ToString(),
                    Line = rawLine,
                    Platform = platform,
                    LineIndex = lineIndex
                };
                _detectedCrashes.Add(crash);
                CrashDetected?.Invoke(crash);
                return crash;
            }
        }

        return null;
    }

    /// <summary>
    /// Scan a batch of log lines. Returns all detected crashes.
    /// </summary>
    public List<CrashEvent> ScanBatch(IEnumerable<string> lines, DevicePlatform platform)
    {
        var results = new List<CrashEvent>();
        int index = 0;
        foreach (var line in lines)
        {
            var crash = ScanLine(line, index++, platform);
            if (crash != null)
                results.Add(crash);
        }
        return results;
    }

    /// <summary>
    /// Extract a crash snippet from the log — N lines before and after the crash point.
    /// </summary>
    public static string ExtractCrashSnippet(IReadOnlyList<string> logLines, int crashIndex, int contextLines = 20)
    {
        if (crashIndex < 0 || crashIndex >= logLines.Count)
            return string.Empty;

        var start = Math.Max(0, crashIndex - contextLines);
        var end = Math.Min(logLines.Count - 1, crashIndex + contextLines);
        var lines = new List<string>();
        for (int i = start; i <= end; i++)
        {
            var marker = i == crashIndex ? ">>> " : "    ";
            lines.Add($"{marker}[{i:D6}] {logLines[i]}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public void Clear()
    {
        lock (_crashLock) { _detectedCrashes.Clear(); }
    }
}
