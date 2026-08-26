using LogPro.Services;

namespace LogPro.Tests.Services;

public class PreferencesStoreTests : IDisposable
{
    private readonly IsolatedPreferences _isolated = new();
    private readonly string _dir;

    public PreferencesStoreTests()
    {
        _dir = _isolated.DirectoryPath;
    }

    [Fact]
    public void Instance_IsIsolatedFromRealSettings()
    {
        var realPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogPro", "settings.json");
        var realBefore = File.Exists(realPath) ? File.ReadAllText(realPath) : null;

        PreferencesService.Current.TargetPackageName = "com.test.isolated";
        PreferencesService.Save();

        File.Exists(Path.Combine(_dir, "settings.json")).Should().BeTrue("saved into the isolated dir");
        if (realBefore == null)
        {
            File.Exists(realPath).Should().BeFalse("real settings must not be created by isolated tests");
        }
        else
        {
            File.ReadAllText(realPath).Should().Be(realBefore, "real settings must be untouched");
        }
    }

    [Fact]
    public void SaveThenReload_RoundTrips()
    {
        PreferencesService.Current.TargetPackageName = "com.roundtrip";
        PreferencesService.Current.LogRetentionDays = 14;
        PreferencesService.Save();

        var reloaded = new PreferencesStore(_dir);
        reloaded.Current.TargetPackageName.Should().Be("com.roundtrip");
        reloaded.Current.LogRetentionDays.Should().Be(14);
    }

    [Fact]
    public void DevicePreferences_AreHashedKeys()
    {
        var pref = PreferencesService.GetDevicePreference("RF8M1234ABCD");
        pref.Notes = "QA device";
        PreferencesService.SaveDevicePreference("RF8M1234ABCD", pref);

        var json = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        json.Should().NotContain("RF8M1234ABCD", "raw serials must never be stored");
        json.Should().Contain("QA device");
    }

    public void Dispose() => _isolated.Dispose();
}
