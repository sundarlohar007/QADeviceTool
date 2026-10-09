using LogPro.Services;

namespace LogPro.Tests;

[Collection("HeavyE2E")]
public class UpdateRecoveryTests
{
    [Fact]
    public async Task FreshUpdater_RecoversPersistedJournal_AndRejectsCorruptBackupPreflight()
    {
        using var isolated = new IsolatedPreferences();
        var root = isolated.DirectoryPath;
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(Path.Combine(tools, "adb"));
        var manifest = Path.Combine(root, ToolManifest.DefaultFileName);
        await File.WriteAllTextAsync(Path.Combine(tools, "adb", "adb.exe"), "old");
        await ToolManifest.WriteAsync(tools, manifest);
        var original = await File.ReadAllTextAsync(manifest);
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        await File.WriteAllTextAsync(Path.Combine(stage, "adb.exe"), "new");
        await new ToolUpdateTransaction(root, tools).CommitAsync("adb", stage);
        await File.WriteAllTextAsync(Path.Combine(root, ".logpro_transaction_adb.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Tool = "adb", RestoreName = "adb", CurrentName = "adb", OriginalManifest = original }));
        using var updater = new UpdateService(tools, root);
        updater.HasPendingTransactions.Should().BeTrue();
        await updater.RecoverPendingTransactionsAsync();
        updater.HasPendingTransactions.Should().BeFalse();
        (await File.ReadAllTextAsync(Path.Combine(tools, "adb", "adb.exe"))).Should().Be("old");
        (await updater.ValidateRollbackAsync("adb")).Success.Should().BeTrue();
        await File.WriteAllTextAsync(Path.Combine(root, ".logpro_backup_adb", "adb.exe"), "corrupt");
        (await updater.ValidateRollbackAsync("adb")).Success.Should().BeFalse();
    }

    [Theory]
    [InlineData("journal-written")]
    [InlineData("old-removed")]
    [InlineData("new-installed")]
    [InlineData("manifest-written")]
    public async Task InterruptedUpdate_RestoresVerifiedBackup(string checkpoint)
    {
        var root = Path.Combine(Path.GetTempPath(), "LogProTransaction_" + Guid.NewGuid().ToString("N"));
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(Path.Combine(tools, "adb"));
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        try
        {
            var installed = Path.Combine(tools, "adb", "adb.exe");
            await File.WriteAllTextAsync(installed, "old");
            await File.WriteAllTextAsync(Path.Combine(stage, "adb.exe"), "new");
            await ToolManifest.WriteAsync(tools, Path.Combine(root, ToolManifest.DefaultFileName));
            var transaction = new ToolUpdateTransaction(root, tools)
            { Checkpoint = point => { if (point == checkpoint) throw new IOException("synthetic interruption"); } };
            await FluentActions.Awaiting(() => transaction.CommitAsync("adb", stage)).Should().ThrowAsync<IOException>();
            (await File.ReadAllTextAsync(installed)).Should().Be("old");
            (await ToolManifest.VerifyAsync(tools, Path.Combine(root, ToolManifest.DefaultFileName))).IsHealthy.Should().BeTrue();
            Directory.GetFiles(root, ".logpro_transaction_*.json").Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Rollback_RepairsMissingTool_WithoutTrustingTamperedOtherTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "LogProRollback_" + Guid.NewGuid().ToString("N"));
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(Path.Combine(tools, "adb"));
        Directory.CreateDirectory(Path.Combine(tools, "scrcpy"));
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tools, "adb", "adb.exe"), "old");
            await File.WriteAllTextAsync(Path.Combine(tools, "scrcpy", "scrcpy.exe"), "unrelated");
            await File.WriteAllTextAsync(Path.Combine(stage, "adb.exe"), "new");
            await ToolManifest.WriteAsync(tools, Path.Combine(root, ToolManifest.DefaultFileName));
            await new ToolUpdateTransaction(root, tools).CommitAsync("adb", stage);
            Directory.Delete(Path.Combine(tools, "adb"), true);
            using var updater = new UpdateService(tools, root);
            var restored = await updater.RollbackLastUpdateAsync("adb");
            restored.Success.Should().BeTrue(restored.Message);
            (await File.ReadAllTextAsync(Path.Combine(tools, "adb", "adb.exe"))).Should().Be("old");
            await File.WriteAllTextAsync(Path.Combine(tools, "scrcpy", "scrcpy.exe"), "tampered");
            (await updater.RollbackLastUpdateAsync("adb")).Success.Should().BeFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Preferences_RejectStaleWriter_AndRestoreBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "LogProConcurrentPrefs_" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new PreferencesStore(root);
            var stale = new PreferencesStore(root);
            first.Current.LogRetentionDays = 30;
            first.Save().Should().BeTrue();
            stale.Current.LogRetentionDays = 1;
            stale.Save().Should().BeFalse();
            first.Current.LogRetentionDays = 60;
            first.Save().Should().BeTrue();
            File.WriteAllText(first.SettingsFilePath, "invalid json");
            new PreferencesStore(root).Current.LogRetentionDays.Should().Be(30);
        }
        finally { Directory.Delete(root, true); }
    }
}
