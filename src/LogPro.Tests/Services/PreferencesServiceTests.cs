using LogPro.Models;
using LogPro.Services;

namespace LogPro.Tests.Services;

public class PreferencesServiceTests
{
    private string _testSettingsPath = null!;
    private string _testAppDataDir = null!;

    public PreferencesServiceTests()
    {
        var tempPath = Path.GetTempPath();
        var testDir = Path.Combine(tempPath, $"LogProTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        _testAppDataDir = Path.Combine(testDir, "QAQCDeviceTool");
        Directory.CreateDirectory(_testAppDataDir);
        _testSettingsPath = Path.Combine(_testAppDataDir, "settings.json");
    }

    [Fact]
    public void AppPreferences_DefaultValues_AreSet()
    {
        var prefs = new AppPreferences();

        prefs.SessionsRootDirectory.Should().BeEmpty();
        prefs.TargetPackageName.Should().BeEmpty();
        prefs.DevicePreferences.Should().NotBeNull();
        prefs.LogRetentionDays.Should().Be(7);
    }

    [Fact]
    public void DevicePreference_DefaultValues_AreSet()
    {
        var pref = new DevicePreference();

        pref.Notes.Should().BeEmpty();
        pref.Tag.Should().BeEmpty();
        pref.LastConnected.Should().BeNull();
    }
}