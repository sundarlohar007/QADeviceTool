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

    [Fact]
    public void ClearAllData_PreservesUnrelatedFoldersInCustomSessionsRoot()
    {
        var root = Path.Combine(_dir, "shared");
        var unrelated = Path.Combine(root, "other-project");
        var completed = Path.Combine(root, "completed-session");
        Directory.CreateDirectory(unrelated);
        Directory.CreateDirectory(completed);
        File.WriteAllText(Path.Combine(unrelated, "important.txt"), "keep");
        WriteSessionMetadata(completed, DateTime.UtcNow.AddDays(-10));
        PreferencesService.Current.SessionsRootDirectory = root;

        var preview = PreferencesService.PreviewClearAllData();
        preview.SessionDirectories.Should().Contain(completed);
        preview.SessionDirectories.Should().NotContain(unrelated);

        var result = PreferencesService.ClearAllData();
        result.Failures.Should().BeEmpty();
        Directory.Exists(completed).Should().BeFalse();
        File.Exists(Path.Combine(unrelated, "important.txt")).Should().BeTrue();
        Directory.Exists(root).Should().BeTrue();
    }

    [Fact]
    public void CleanupOldSessions_OnlyDeletesCompletedOwnedSessionsPastRetention()
    {
        var root = Path.Combine(_dir, "shared");
        var old = Path.Combine(root, "old-session");
        var recent = Path.Combine(root, "recent-session");
        var active = Path.Combine(root, "active-session");
        var unrelated = Path.Combine(root, "other-project");
        foreach (var directory in new[] { old, recent, active, unrelated }) Directory.CreateDirectory(directory);
        WriteSessionMetadata(old, DateTime.UtcNow.AddDays(-10));
        WriteSessionMetadata(recent, DateTime.UtcNow.AddDays(-1));
        WriteSessionMetadata(active, null);
        Directory.SetLastWriteTime(active, DateTime.Now.AddDays(-30));
        Directory.SetLastWriteTime(unrelated, DateTime.Now.AddDays(-30));
        PreferencesService.Current.SessionsRootDirectory = root;
        PreferencesService.Current.LogRetentionDays = 7;

        PreferencesService.CleanupOldSessions();

        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(recent).Should().BeTrue();
        Directory.Exists(active).Should().BeTrue();
        Directory.Exists(unrelated).Should().BeTrue();
    }

    [Fact]
    public void LoadingNullAndInvalidSettings_UsesSafeDefaults()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"SessionsRootDirectory":"\\\\network\\share","LogRetentionDays":-1,"UpdatePreferences":null,"ThemePreference":"Other"}""");
        var reloaded = new PreferencesStore(_dir);
        reloaded.Current.SessionsRootDirectory.Should().Be(LogPro.Helpers.PathHelper.GetDefaultSessionsDirectory());
        reloaded.Current.LogRetentionDays.Should().Be(7);
        reloaded.Current.UpdatePreferences.Should().NotBeNull();
        reloaded.Current.ThemePreference.Should().Be("Dark");
    }

    private static void WriteSessionMetadata(string directory, DateTime? ended)
    {
        var end = ended is DateTime time ? $"\"{time:O}\"" : "null";
        File.WriteAllText(Path.Combine(directory, "session.json"),
            $"{{\"Id\":\"1234abcd\",\"Name\":\"session\",\"StartTime\":\"{DateTime.UtcNow.AddDays(-20):O}\",\"EndTime\":{end}}}");
    }

    public void Dispose() => _isolated.Dispose();
}
