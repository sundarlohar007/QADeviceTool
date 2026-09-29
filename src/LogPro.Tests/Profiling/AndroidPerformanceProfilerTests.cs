using LogPro.Services;
using LogPro.Services.Profiling;
using Moq;

namespace LogPro.Tests.Profiling;

public class AndroidPerformanceProfilerTests
{
    [Fact]
    public async Task SampleOnce_UsesExactPackageLayerAndCountsOnlyNewFrames()
    {
        const string layer = "SurfaceView[com.game/MainActivity]#2";
        var present = new[] { 1_000_000_000L, 1_016_666_666L, 1_050_000_000L };
        var adb = new Mock<IAdbService>();
        adb.Setup(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serial, string command, CancellationToken token) =>
            {
                if (command.EndsWith("SurfaceFlinger --list")) return "SurfaceView[other/app]#1\n" + layer + "\n";
                if (command.Contains("SurfaceFlinger --latency"))
                    return "16666666\n" + string.Join("\n", present.Select(p => $"1\t2\t{p}"));
                return string.Empty;
            });
        await using var profiler = new AndroidPerformanceProfiler(adb.Object, "S1", "com.game");

        var first = await profiler.SampleOnceAsync();
        first.JankyFrames.Should().Be(0);
        present = new[] { present[0], present[1], present[2], 1_083_333_333L };
        var second = await profiler.SampleOnceAsync();
        second.JankyFrames.Should().Be(1);
        second.TotalFrames.Should().Be(1);
        var third = await profiler.SampleOnceAsync();
        third.JankyFrames.Should().Be(0);
        third.TotalFrames.Should().Be(0);
        third.Fps.Should().BeNull("no new frames means the previous FPS is stale");
        adb.Verify(a => a.ExecuteCommandAsync("S1", $"shell dumpsys SurfaceFlinger --latency \"{layer}\"", It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
