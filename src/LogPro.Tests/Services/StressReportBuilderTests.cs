using LogPro.Services;

namespace LogPro.Tests.Services;

public class StressReportBuilderTests
{
    [Fact]
    public void ParseMetrics_ExtractsEssentialCpuMemoryAndFrameStats()
    {
        var meminfo = """
            Applications Memory Usage (in Kilobytes):
            TOTAL PSS:    184321
            TOTAL RSS:    250000
            """;
        var cpuinfo = "  12% 1234/com.example.app: 8% user + 4% kernel";
        var gfxinfo = """
            Janky frames: 14 (7.00%)
            90th percentile: 20ms
            Number Missed Vsync: 3
            """;

        var metrics = StressReportBuilder.ParseMetrics("com.example.app", meminfo, cpuinfo, gfxinfo);

        metrics.TotalPssKb.Should().Be(184321);
        metrics.CpuLine.Should().Be("12% 1234/com.example.app: 8% user + 4% kernel");
        metrics.JankyFrames.Should().Be(14);
        metrics.FrameP90Ms.Should().Be(20);
    }

    [Fact]
    public void BuildReport_IncludesHumanReadableOutcome()
    {
        var report = StressReportBuilder.BuildReport(new StressRunSummary
        {
            PackageName = "com.example.app",
            DeviceName = "Pixel Test",
            EventCount = 1000,
            EventsInjected = 998,
            CrashCount = 1,
            AnrCount = 0,
            Duration = TimeSpan.FromSeconds(42),
            Metrics = new StressPerformanceMetrics { TotalPssKb = 184321, JankyFrames = 14 }
        });

        report.Should().Contain("Stress Test Report");
        report.Should().Contain("Result: FAILED");
        report.Should().Contain("Memory PSS: 184321 KB");
        report.Should().Contain("Janky Frames: 14");
    }

    [Theory]
    [InlineData("Completed", 0, 1000, 999, 0, 0, "INCOMPLETE")]
    [InlineData("Completed", 1, 1000, 1000, 0, 0, "ERROR")]
    [InlineData("Completed", 0, 1000, 1000, 1, 0, "FAILED")]
    [InlineData("Error", 1, 1000, 500, 1, 0, "FAILED")]
    [InlineData("Cancelled", -1, 1000, 400, 0, 0, "CANCELLED")]
    [InlineData("DurationComplete", -1, 400, 400, 0, 0, "PASSED")]
    [InlineData("DurationComplete", -1, 0, 0, 0, 0, "INCOMPLETE")]
    public void Result_ReflectsCompletionAndFailures(string outcome, int exitCode, int budget,
        int injected, int crashes, int anrs, string expected)
    {
        var summary = new StressRunSummary
        {
            Outcome = outcome,
            ExitCode = exitCode,
            EventCount = budget,
            EventsInjected = injected,
            CrashCount = crashes,
            AnrCount = anrs
        };
        summary.Result.Should().Be(expected);
    }

    [Fact]
    public void BuildReport_ShowsHoursWithoutWrapping()
    {
        var report = StressReportBuilder.BuildReport(new StressRunSummary
        {
            Duration = TimeSpan.FromHours(27),
            EventCount = 1,
            EventsInjected = 1
        });
        report.Should().Contain("Duration: 27:00:00");
    }
}
