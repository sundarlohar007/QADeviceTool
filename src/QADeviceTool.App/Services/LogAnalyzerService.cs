using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace LogPro.Services;

/// <summary>
/// Pattern-based log analyzer. Detects custom regex patterns in log streams.
/// Game QA teams define patterns relevant to their game.
/// </summary>
public class LogAnalyzerService
{
    public ObservableCollection<AlertRule> Rules { get; } = new();

    public event Action<AlertRule, string, int>? RuleMatched;

    public LogAnalyzerService()
    {
        // Pre-built common game patterns
        AddDefaultRules();
    }

    private void AddDefaultRules()
    {
        Rules.Add(new AlertRule("OutOfMemory", @"OutOfMemoryError|low memory", "Memory pressure detected"));
        Rules.Add(new AlertRule("Texture Fail", @"texture.*fail|shader.*error|Texture.*null", "Rendering/resource issue"));
        Rules.Add(new AlertRule("Network Timeout", @"timeout|HTTP 5\d\d|Connection.*refused|SocketException", "Network error"));
        Rules.Add(new AlertRule("Auth Fail", @"auth.*fail|login.*fail|401|403", "Authentication issue"));
        Rules.Add(new AlertRule("Null Reference", @"NullReferenceException|null reference|null ptr", "Null reference bug"));
        Rules.Add(new AlertRule("File Not Found", @"FileNotFoundException|No such file|ENOENT", "Missing file or resource"));
    }

    /// <summary>
    /// Scan a single log line against all active rules.
    /// </summary>
    public List<(AlertRule Rule, string Line, int Index)> ScanLine(string line, int lineIndex)
    {
        var matches = new List<(AlertRule, string, int)>();
        if (string.IsNullOrWhiteSpace(line)) return matches;

        foreach (var rule in Rules)
        {
            if (!rule.IsEnabled) continue;
            if (rule.Regex.IsMatch(line))
            {
                matches.Add((rule, line, lineIndex));
                RuleMatched?.Invoke(rule, line, lineIndex);
            }
        }
        return matches;
    }

    public void AddRule(string name, string pattern, string description)
    {
        Rules.Add(new AlertRule(name, pattern, description));
    }

    public void ClearCustomRules()
    {
        for (int i = Rules.Count - 1; i >= 0; i--)
        {
            if (!Rules[i].IsPreBuilt)
                Rules.RemoveAt(i);
        }
    }
}

/// <summary>
/// A single alert rule with regex pattern.
/// </summary>
public class AlertRule
{
    private Regex? _cachedRegex;
    private string _pattern = string.Empty;

    public string Name { get; set; }
    public string Pattern
    {
        get => _pattern;
        set { _pattern = value; _cachedRegex = null; }
    }
    public string Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsPreBuilt { get; set; }

    public Regex Regex
    {
        get
        {
            if (_cachedRegex == null)
            {
                try
                {
                    _cachedRegex = new Regex(Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
                catch (ArgumentException)
                {
                    // Return a never-match regex if pattern is invalid
                    _cachedRegex = new Regex(@"^\b$", RegexOptions.Compiled);
                }
            }
            return _cachedRegex;
        }
    }

    public AlertRule(string name, string pattern, string description, bool isPreBuilt = true)
    {
        Name = name;
        Pattern = pattern;
        Description = description;
        IsPreBuilt = isPreBuilt;
    }
}
