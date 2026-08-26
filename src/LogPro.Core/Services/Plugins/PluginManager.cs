using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogPro.Services.Plugins;

/// <summary>
/// Plugin discovery + loading (§16). A plugin directory contains one subfolder per plugin
/// with a plugin.json manifest. Two loader kinds:
///  - declarative regex rules (no code, cross-platform, sandbox-free by construction)
///  - .NET assemblies implementing ILogParserPlugin (loaded in an isolated AssemblyLoadContext;
///    trust model: plugins are same-user local files — documented, not sandboxed in v1)
/// </summary>
public sealed class PluginManager
{
    private readonly List<IPlugin> _plugins = new();
    private readonly Dictionary<string, ILogParserPlugin> _parsers = new(StringComparer.Ordinal);

    public IReadOnlyList<IPlugin> Plugins => _plugins;
    public IReadOnlyDictionary<string, ILogParserPlugin> LogParsers => _parsers;

    public void LoadPlugins(string pluginsDir)
    {
        if (!Directory.Exists(pluginsDir)) return;

        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath)) continue;

            PluginManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(
                    File.ReadAllText(manifestPath), LogProJsonContext.Default.PluginManifest);
            }
            catch (JsonException ex)
            {
                AppLogger.Log.Warn(ex, $"[Plugins] Skipping invalid manifest: {manifestPath}");
                continue;
            }
            if (manifest == null) continue;

            try { LoadPlugin(dir, manifest); }
            catch (Exception ex)
            {
                AppLogger.Log.Warn(ex, $"[Plugins] Failed to load plugin '{manifest.Id}'");
            }
        }
    }

    private void LoadPlugin(string dir, PluginManifest manifest)
    {
        if (_plugins.Any(p => p.Id == manifest.Id))
            throw new InvalidOperationException($"duplicate plugin id: {manifest.Id}");

        if (manifest.Type != "logParser")
        {
            AppLogger.Log.Warn($"[Plugins] Unsupported plugin type '{manifest.Type}' for '{manifest.Id}' — skipped");
            return;
        }

        ILogParserPlugin? parser = null;

        if (manifest.RegexRules is { Count: > 0 })
        {
            parser = new RegexLogParser(manifest, manifest.RegexRules);
        }
        else if (manifest.EntryAssembly != null)
        {
            parser = LoadAssemblyPlugin(dir, manifest);
        }

        if (parser == null) return;

        _plugins.Add(parser);
        _parsers[parser.Id] = parser;
        AppLogger.Log.Info($"[Plugins] Loaded '{parser.Id}' v{parser.Version}");
    }

    private static ILogParserPlugin? LoadAssemblyPlugin(string dir, PluginManifest manifest)
    {
        var assemblyPath = Path.Combine(dir, manifest.EntryAssembly!);
        if (!File.Exists(assemblyPath)) return null;

        var alc = new AssemblyLoadContext($"plugin:{manifest.Id}", isCollectible: true);
        var assembly = alc.LoadFromAssemblyPath(assemblyPath);
        var typeName = manifest.EntryType ?? "Plugin";
        var type = assembly.GetType(typeName)
                   ?? assembly.GetTypes().FirstOrDefault(t =>
                       typeof(ILogParserPlugin).IsAssignableFrom(t) && !t.IsAbstract);

        if (type == null || Activator.CreateInstance(type) is not ILogParserPlugin plugin)
            return null;
        return plugin;
    }
}

/// <summary>Declarative regex parser — no code required.</summary>
internal sealed class RegexLogParser : ILogParserPlugin
{
    private readonly List<(Regex Regex, string Level, string? TagGroup, string? MessageGroup)> _rules;

    public RegexLogParser(PluginManifest manifest, IEnumerable<RegexRule> rules)
    {
        Id = manifest.Id;
        Name = manifest.Name;
        Version = manifest.Version;
        Type = manifest.Type;
        _rules = rules.Select(r => (
            new Regex(r.Pattern, RegexOptions.Compiled),
            r.Level,
            string.IsNullOrWhiteSpace(r.TagGroup) ? null : r.TagGroup,
            string.IsNullOrWhiteSpace(r.MessageGroup) ? null : r.MessageGroup)).ToList();
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Type { get; }

    public bool TryParse(string rawLine, out ParsedLogEntry entry)
    {
        foreach (var (regex, level, tagGroup, messageGroup) in _rules)
        {
            var match = regex.Match(rawLine);
            if (!match.Success) continue;

            var tag = tagGroup != null && match.Groups[tagGroup].Success ? match.Groups[tagGroup].Value : string.Empty;
            var message = messageGroup != null && match.Groups[messageGroup].Success ? match.Groups[messageGroup].Value : rawLine;
            entry = new ParsedLogEntry(level, tag, message);
            return true;
        }

        entry = default!;
        return false;
    }
}
