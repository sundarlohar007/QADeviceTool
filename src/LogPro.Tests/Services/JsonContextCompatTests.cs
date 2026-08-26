using System.Text.Json;
using LogPro.Services;

namespace LogPro.Tests.Services;

public class JsonContextCompatTests
{
    [Fact]
    public void SourceGenContext_DeserializesPascalCaseSettings()
    {
        const string pascal = """{ "SessionsRootDirectory": "X", "ThemePreference": "Light", "SecureMode": true }""";
        var prefs = JsonSerializer.Deserialize(pascal, LogProJsonContext.Default.AppPreferences);
        prefs.Should().NotBeNull();
        prefs!.ThemePreference.Should().Be("Light", "camelCase policy must still accept PascalCase input (case-insensitive)");
    }

    [Fact]
    public void SourceGenContext_RoundTrips()
    {
        var prefs = new AppPreferences { ThemePreference = "Light", SecureMode = true, LogRetentionDays = 7 };
        var json = JsonSerializer.Serialize(prefs, LogProJsonContext.Default.AppPreferences);
        var back = JsonSerializer.Deserialize(json, LogProJsonContext.Default.AppPreferences);
        back!.ThemePreference.Should().Be("Light");
    }
}
