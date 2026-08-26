using System.Text.Json.Serialization;

namespace LogPro.Services.Plugins;

/// <summary>plugin.json manifest (§16).</summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Type { get; set; } = string.Empty;           // "logParser" (extensible)
    public string? EntryAssembly { get; set; }                  // .NET plugin assembly
    public string? EntryType { get; set; }                     // full type name implementing ILogParserPlugin
    public List<RegexRule>? RegexRules { get; set; }            // declarative parser (no code)
    public string? Description { get; set; }
}

public sealed class RegexRule
{
    public string Pattern { get; set; } = string.Empty;
    public string Level { get; set; } = "Info";                // Fatal/Error/Warning/Info/Debug/Verbose/Unknown
    public string? TagGroup { get; set; }                       // named group for the tag
    public string? MessageGroup { get; set; }                  // named group for the message (default: whole line)
}
