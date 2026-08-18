using LogPro.Services;
using Moq;

namespace LogPro.Tests.Services;

public class ConditionPlannersTests
{
    [Fact]
    public void PlanRoute_InterpolatesBetweenWaypoints()
    {
        var waypoints = new[] { (0.0, 0.0), (0.001, 0.0) }; // ~111 m apart
        var fixes = ConditionPlanners.PlanRoute(waypoints, speedMetersPerSecond: 10, duration: TimeSpan.FromSeconds(5));

        fixes.Should().HaveCount(5, "one fix per second");
        fixes[0].OffsetSeconds.Should().Be(0);
        fixes[^1].OffsetSeconds.Should().Be(4);
        fixes[0].Latitude.Should().Be(0.0);
        // ~40m travelled at 10 m/s over ~111m segment → latitude ≈ 40/111 × 0.001
        fixes[4].Latitude.Should().BeApproximately(0.00036, 0.00005);
        fixes[4].Longitude.Should().Be(0.0);
    }

    [Fact]
    public void PlanRoute_LoopsWhenDurationExceedsRoute()
    {
        var waypoints = new[] { (0.0, 0.0), (0.0, 0.0005) }; // ~55.6 m
        var fixes = ConditionPlanners.PlanRoute(waypoints, speedMetersPerSecond: 30, duration: TimeSpan.FromSeconds(4));
        fixes.Should().HaveCount(4);
        // 120m total travel > 55.6m route → wraps around
        fixes[2].Longitude.Should().BeLessThan(fixes[1].Longitude, "route wraps after covering the segment");
    }

    [Fact]
    public void BuildNetemScript_ContainsDelayLossAndBandwidth()
    {
        var script = ConditionPlanners.BuildNetemScript(
            ConditionPlanners.Presets.Single(p => p.Name == "3g"), "wlan0");
        script.Should().Contain("delay 150ms 30ms");
        script.Should().Contain("loss 2%");
        script.Should().Contain("tbf rate 1500kbit");
        script.Should().Contain("dev wlan0");
    }

    [Fact]
    public void ResetScript_DeletesQdisc()
    {
        ConditionPlanners.BuildNetemResetScript("wlan0").Should().Contain("tc qdisc del dev wlan0 root");
    }
}

public class ConditionSimulatorTests
{
    private static (ConditionSimulator Sim, Mock<IAdbService> Adb) Create()
    {
        var adb = new Mock<IAdbService>();
        adb.Setup(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string s, string c) => string.Empty);
        return (new ConditionSimulator(adb.Object), adb);
    }

    [Fact]
    public async Task SetAndResetMockLocation_BuildExpectedCommands()
    {
        var (sim, adb) = Create();
        await sim.SetMockLocationAppAsync("S1", "com.game");
        await sim.ResetLocationAsync("S1", "com.game");

        adb.Verify(a => a.ExecuteCommandAsync("S1", "shell appops set com.game android:mock_location allow"), Times.Once);
        adb.Verify(a => a.ExecuteCommandAsync("S1", "shell appops set com.game android:mock_location deny"), Times.Once);
    }

    [Fact]
    public async Task ApplyNetwork_NoRoot_ReturnsFalse()
    {
        var adb = new Mock<IAdbService>();
        adb.Setup(a => a.ExecuteCommandAsync("S1", "shell su -c id")).ReturnsAsync("not root");
        var sim = new ConditionSimulator(adb.Object);

        (await sim.ApplyNetworkConditionAsync("S1", ConditionPlanners.Presets[2], "wlan0")).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyNetwork_Root_AppliesScript()
    {
        var adb = new Mock<IAdbService>();
        adb.Setup(a => a.ExecuteCommandAsync("S1", "shell su -c id")).ReturnsAsync("uid=0(root)");
        adb.Setup(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.Is<string>(c => c != "shell su -c id")))
            .ReturnsAsync(string.Empty);
        var sim = new ConditionSimulator(adb.Object);

        (await sim.ApplyNetworkConditionAsync("S1", ConditionPlanners.Presets[0], "wlan0")).Should().BeTrue();
        adb.Verify(a => a.ExecuteCommandAsync(It.IsAny<string>(),
            It.Is<string>(c => c.Contains("netem delay 300ms 80ms"))), Times.Once);
    }

    [Fact]
    public async Task InjectFix_FormatsCoordinates()
    {
        var (sim, adb) = Create();
        await sim.InjectFixAsync("S1", 51.5074, -0.1278);
        adb.Verify(a => a.ExecuteCommandAsync(It.IsAny<string>(),
            It.Is<string>(c => c.Contains("51.507400") && c.Contains("-0.127800"))), Times.Once);
    }
}
