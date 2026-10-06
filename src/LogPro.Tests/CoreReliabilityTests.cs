using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;
using Moq;

namespace LogPro.Tests;

public class CoreReliabilityTests
{
    [Fact]
    public void LogBatches_WorkWithRealWpfCollectionViewAndListBox()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var items = new BulkObservableCollection<string>();
                var view = new ListCollectionView(items);
                var list = new ListBox { ItemsSource = view };
                items.AddRange(new[] { "one", "two", "three" });
                list.SelectedItem = "two";
                items.AddRange(new[] { "four", "five" });
                list.Items.Count.Should().Be(5);
                list.SelectedItem.Should().Be("two");
                items.RemoveRange(0, 1);
                list.Items.Count.Should().Be(4);
                list.SelectedItem.Should().Be("two");
                view.Cast<string>().Should().Equal("two", "three", "four", "five");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
        failure.Should().BeNull();
    }

    [Fact]
    public void FullDisplayBatch_DoesNotConsumeTheFollowingLine()
    {
        var queue = new ConcurrentQueue<string>(Enumerable.Range(0, 2001).Select(i => i.ToString()));
        var batch = SessionService.DrainDisplayBatch(queue, 2000);
        batch.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2000);
        queue.Should().ContainSingle().Which.Should().Be("2000");
    }

    [Theory]
    [InlineData("09-30 12:34:56.789  1234  5678 E Example: failed", "1234", true)]
    [InlineData("09-30 12:34:56.789  9876  5678 E Example: pid 1234 failed", "1234", false)]
    [InlineData("09-30 12:34:56.789  1234  5678 E Example: failed", "", false)]
    public void AppLogFilter_UsesPidField(string line, string pid, bool expected) =>
        SessionService.MatchesLogcatPid(line, pid, LogcatFormat.ThreadTime).Should().Be(expected);

    [Theory]
    [InlineData("logpro", "LogPro_v3.2.166.exe", true)]
    [InlineData("logpro", "LogPro_v3.2.166_portable.zip", false)]
    [InlineData("adb", "LogPro-tool-adb-36.0.2-win-x64.zip", true)]
    [InlineData("pymobiledevice3", "pymobiledevice3.exe", false)]
    [InlineData("pymobiledevice3", "LogPro-tool-pymobiledevice3-9.12.0-win-x64.zip", true)]
    public void Updates_SelectOnlyCompleteWindowsPackages(string tool, string name, bool expected) =>
        UpdateService.IsCompatibleAsset(tool, name).Should().Be(expected);

    [Fact]
    public async Task AndroidResults_ArriveBeforeSlowIosDiscovery()
    {
        var iosResult = new TaskCompletionSource<(bool, List<DeviceInfo>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new Mock<IAdbService>();
        var ios = new Mock<IIosService>();
        adb.Setup(x => x.GetConnectedDevicesWithStatusAsync()).ReturnsAsync((true, new List<DeviceInfo>
        { new() { Serial = "USB-A", Platform = DevicePlatform.Android, ConnectionState = DeviceConnectionState.Online } }));
        ios.Setup(x => x.GetConnectedDevicesWithStatusAsync()).Returns(iosResult.Task);
        using var monitor = new DeviceMonitorService(adb.Object, ios.Object);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += devices => { if (devices.Any(d => d.Serial == "USB-A")) changed.TrySetResult(); };
        var poll = monitor.PollDevicesAsync();
        try
        {
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            poll.IsCompleted.Should().BeFalse();
        }
        finally { iosResult.TrySetResult((true, new())); await poll; }
    }

    [Fact]
    public async Task FailedDiscovery_RetainsIdentityButDisablesActions()
    {
        var adb = new Mock<IAdbService>();
        var ios = new Mock<IIosService>();
        adb.SetupSequence(x => x.GetConnectedDevicesWithStatusAsync())
            .ReturnsAsync((true, new List<DeviceInfo> { new() { Serial = "A", Platform = DevicePlatform.Android, ConnectionState = DeviceConnectionState.Online } }))
            .ReturnsAsync((false, new List<DeviceInfo>()));
        ios.Setup(x => x.GetConnectedDevicesWithStatusAsync()).ReturnsAsync((true, new List<DeviceInfo>()));
        using var monitor = new DeviceMonitorService(adb.Object, ios.Object);
        await monitor.PollDevicesAsync();
        await monitor.PollDevicesAsync();
        monitor.CurrentDevices.Should().ContainSingle().Which.IsTemporarilyUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task LegacyPlaceholderDoesNotInvalidateOtherwiseVerifiedTools()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LogProManifestTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var manifest = Path.Combine(dir, "manifest.json");
            File.WriteAllText(Path.Combine(dir, "tool.exe"), "verified tool");
            await ToolManifest.WriteAsync(dir, manifest);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<ToolManifestEntry>>(File.ReadAllText(manifest), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            entries.Add(new ToolManifestEntry { Path = ".gitkeep", Sha256 = new string('0', 64) });
            File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(entries));
            (await ToolManifest.VerifyAsync(dir, manifest)).IsHealthy.Should().BeTrue();
            File.WriteAllText(Path.Combine(dir, "tool.exe"), "modified tool");
            (await ToolManifest.VerifyAsync(dir, manifest)).IsHealthy.Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }
}
