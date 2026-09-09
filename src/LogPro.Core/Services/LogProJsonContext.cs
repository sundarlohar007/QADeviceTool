using System.Text.Json.Serialization;
using LogPro.Models;

namespace LogPro.Services;

/// <summary>
/// System.Text.Json source-generated context — required for NativeAOT/trimming (§6.1).
/// Every type serialized via JsonSerializer must be registered here.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppPreferences))]
[JsonSerializable(typeof(DevicePreference))]
[JsonSerializable(typeof(MacroFile))]
[JsonSerializable(typeof(MacroEvent))]
[JsonSerializable(typeof(SimpleMacroStep))]
[JsonSerializable(typeof(IReadOnlyList<ToolManifestEntry>))]
[JsonSerializable(typeof(Plugins.PluginManifest))]
[JsonSerializable(typeof(VerificationCache))]
[JsonSerializable(typeof(FileMetadata))]
[JsonSerializable(typeof(Dictionary<string, FileMetadata>))]
public partial class LogProJsonContext : JsonSerializerContext
{
}
