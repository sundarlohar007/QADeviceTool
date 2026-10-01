using LogPro.Services.Profiling;
using Moq;

namespace LogPro.Tests.Profiling;

public class SoakRunnerTests
{
    private static Mock<LogPro.Services.IAdbService> CreateFakeAdb()
    {
        var mock = new Mock<LogPro.Services.IAdbService>();
        var latencyCalls = 0;
        mock.Setup(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string serial, string command, CancellationToken _) => command switch
            {
                var c when c.Contains("SurfaceFlinger --list") =>
                    "SurfaceView[com.fakegame/com.fakegame.MainActivity](BLAST)#0\n",
                var c when c.Contains("SurfaceFlinger --latency") => SurfaceFlingerOutput(Interlocked.Increment(ref latencyCalls)),
                var c when c.Contains("cpuinfo") =>
                    "  38% 2345/com.fakegame: 25% user + 13% kernel / faults: 42 minor\n",
                var c when c.Contains("meminfo") =>
                    "     TOTAL PSS:    384000\n     TOTAL RSS:    462000\n",
                var c when c.Contains("thermalservice") => "Thermal status: 0\n",
                var c when c.Contains("battery") => "level: 87\n",
                _ => string.Empty
            });
        return mock;
    }

    private static string SurfaceFlingerOutput(int extraFrames)
    {
        var sb = new System.Text.StringBuilder("16666666\n");
        long present = 10_000_000_000L;
        for (var i = 0; i < 60 + extraFrames; i++)
        {
            present += 16_666_666L;
            if (i % 10 == 0) present += 20_000_000L;
            sb.AppendLine($"{i * 16_666_666:D14}\t{i * 16_666_666 + 2_000_000:D14}\t{present:D14}");
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Run_CollectsSamplesAndFlags()
    {
        var adb = CreateFakeAdb();
        var loadCalls = 0;
        var load = async (CancellationToken token) =>
        {
            Interlocked.Increment(ref loadCalls);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        var report = await SoakRunner.RunAsync(
            adb.Object, "FAKE01", "com.fakegame", TimeSpan.FromSeconds(3), load);

        report.SampleCount.Should().BeGreaterThanOrEqualTo(1);
        report.Duration.Should().Be(TimeSpan.FromSeconds(3));
        report.AvgFpsStart.Should().HaveValue();
        report.JankyFrames.Should().BeGreaterThanOrEqualTo(0);
        loadCalls.Should().Be(1, "load loop invoked exactly once");
        report.HasIssues.Should().BeFalse("stable synthetic stream must not flag");
    }

    [Fact]
    public async Task Run_MemoryGrowth_IsFlagged()
    {
        var mock = new Mock<LogPro.Services.IAdbService>();
        var memKb = 100_000;
        mock.Setup(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, string c, CancellationToken _) =>
            {
                if (c.Contains("SurfaceFlinger --list")) return "SurfaceView[x]#0\n";
                if (c.Contains("SurfaceFlinger --latency")) return "16666666\n1000\t1000\t10000000000\n";
                if (c.Contains("cpuinfo")) return " 1% 1/x: 0% user + 0% kernel\n";
                if (c.Contains("meminfo")) return $"     TOTAL PSS:    {Interlocked.Add(ref memKb, 200_000)}\n";
                if (c.Contains("thermalservice")) return "Thermal status: 0\n";
                if (c.Contains("battery")) return "level: 90\n";
                return string.Empty;
            });

        var report = await SoakRunner.RunAsync(mock.Object, "FAKE01", "com.fakegame",
            TimeSpan.FromSeconds(2), token => Task.Delay(Timeout.InfiniteTimeSpan, token), sampleIntervalMs: 300);

        report.MemoryGrowthFlagged.Should().BeTrue("+200MB per sample exceeds the 150MB flag");
        report.HasIssues.Should().BeTrue();
    }

    [Fact]
    public async Task Run_NoLayer_FpsNullButStable()
    {
        var mock = new Mock<LogPro.Services.IAdbService>();
        mock.Setup(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string s, string c, CancellationToken _) => c.Contains("SurfaceFlinger --list") ? "no layers\n" : string.Empty);

        var report = await SoakRunner.RunAsync(mock.Object, "FAKE01", "", TimeSpan.FromSeconds(1), _ => Task.CompletedTask);

        report.LoadCompletedEarly.Should().BeTrue("the supplied load returned before the soak window ended");
        report.AvgFpsStart.Should().BeNull("no resolvable SurfaceFlinger layer");
        report.HasIssues.Should().BeTrue("missing metrics and early load completion cannot pass a soak run");
        report.HasSufficientData.Should().BeFalse();
    }

    [Fact]
    public async Task Run_LoadFault_IsReportedInsteadOfPassing()
    {
        var report = await SoakRunner.RunAsync(CreateFakeAdb().Object, "FAKE01", "com.fakegame",
            TimeSpan.FromSeconds(2), _ => throw new InvalidOperationException("load failed"), sampleIntervalMs: 250);
        report.LoadError.Should().Contain("load failed");
        report.HasIssues.Should().BeTrue();
    }
}
