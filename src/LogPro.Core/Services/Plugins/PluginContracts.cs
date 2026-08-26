namespace LogPro.Services.Plugins;

/// <summary>Base contract every plugin implements.</summary>
public interface IPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Type { get; }
}

/// <summary>Parsed log entry produced by a parser plugin.</summary>
public sealed record ParsedLogEntry(string Level, string Tag, string Message);

/// <summary>Log-parser extension point (§16): transforms raw device log lines.</summary>
public interface ILogParserPlugin : IPlugin
{
    bool TryParse(string rawLine, out ParsedLogEntry entry);
}
