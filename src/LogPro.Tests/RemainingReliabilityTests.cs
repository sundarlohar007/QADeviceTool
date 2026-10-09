using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;
using LogPro.Services.Profiling;

namespace LogPro.Tests;

public class RemainingReliabilityTests
{
    [Fact]
    public void LongRecords_KeepSeverityAcrossMessageLines_AndResetBetweenRecords()
    {
        var parser = new LogStreamParser(LogcatFormat.Long);
        parser.Parse("[ 10-06 12:00:00.000 123: 456 E/Tag ]");
        var first = parser.Parse("first message line");
        var second = parser.Parse("second message line");
        first.Level.Should().Be(LogLevel.Error);
        second.Tag.Should().Be("Tag");
        second.Timestamp.Should().Be("10-06 12:00:00.000");
        parser.Parse("");
        parser.Parse("unattributed").Level.Should().Be(LogLevel.Unknown);
        new LogStreamParser(LogcatFormat.Raw).Parse("ERROR is payload text").Level.Should().Be(LogLevel.Unknown);
    }

    [Fact]
    public void FullRunStatistics_SurviveChartAndSampleHistoryLimits()
    {
        var statistics = new ProfilerAccumulator();
        for (var i = 0; i < 30000; i++) statistics.Add(new ProfilerSnapshot
        {
            Fps = i < 2 ? 10 : 60,
            TotalFrames = 1,
            FrameTimeP90Ms = i < 2 ? 100 : 16,
            PssKb = 100 + i,
            CpuPercent = i == 0 ? 99 : 1,
            JankyFrames = i < 2 ? 1 : 0
        });
        var summary = statistics.Summary;
        statistics.Count.Should().Be(30000);
        summary.MemoryGrowthKb.Should().Be(29999);
        summary.MinFps.Should().Be(10);
        summary.MaxCpuPercent.Should().Be(99);
        summary.SlowSession.Should().BeTrue();
        summary.JankyFrames.Should().Be(2);
    }

    [Theory]
    [InlineData("ConnectionResetError", DocumentTransferOutcome.Disconnected)]
    [InlineData("ApplicationLookupFailed", DocumentTransferOutcome.Unsupported)]
    [InlineData("PairingDialogResponsePending", DocumentTransferOutcome.TrustRequired)]
    [InlineData("OBJECT_NOT_FOUND", DocumentTransferOutcome.NotFound)]
    public void DocumentFailure_PreservesActionableCause(string error, DocumentTransferOutcome expected)
        => DocumentTransferResult.FromTool(new ToolLauncherResult { Error = error }, CancellationToken.None).Outcome.Should().Be(expected);

    [Fact]
    public async Task Preferences_ConcurrentTransactionsKeepEveryChange_AndDetachDeviceDrafts()
    {
        using var isolated = new IsolatedPreferences();
        var store = new PreferencesStore(Path.Combine(isolated.DirectoryPath, "prefs"));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            store.Update(p => p.UpdatePreferences.SuppressedVersions.Add("tool:" + i)).Should().BeTrue())));
        new PreferencesStore(Path.GetDirectoryName(store.SettingsFilePath)).Current.UpdatePreferences.SuppressedVersions.Should().HaveCount(32);
        store.TrySaveDevicePreference("device", new DevicePreference { Notes = "saved" }).Should().BeTrue();
        store.GetDevicePreference("device").Notes = "unsaved";
        store.GetDevicePreference("device").Notes.Should().Be("saved");
    }
}
