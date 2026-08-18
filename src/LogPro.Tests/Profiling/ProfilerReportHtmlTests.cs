using LogPro.Services.Profiling;

namespace LogPro.Tests.Profiling;

public class ProfilerReportHtmlTests
{
    private static List<ProfilerSnapshot> Snapshots() => new()
    {
        new() { Timestamp = new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc), Fps = 60.0, FrameTimeP90Ms = 16.7, CpuPercent = 30.0, PssKb = 300_000, JankyFrames = 1, ThermalStatus = 0, BatteryLevel = 90 },
        new() { Timestamp = new DateTime(2026, 8, 14, 10, 0, 1, DateTimeKind.Utc), Fps = 59.0, FrameTimeP90Ms = 16.9, CpuPercent = 32.0, PssKb = 302_000, JankyFrames = 2, ThermalStatus = 0, BatteryLevel = 89 },
        new() { Timestamp = new DateTime(2026, 8, 14, 10, 0, 2, DateTimeKind.Utc), Fps = 40.0, FrameTimeP90Ms = 25.0, CpuPercent = 45.0, PssKb = 310_000, JankyFrames = 5, ThermalStatus = 0, BatteryLevel = 88 },
    };

    [Fact]
    public void Render_ContainsSummaryAndSamples()
    {
        var html = ProfilerReportHtml.Render("Test Run", Snapshots());
        html.Should().Contain("<!DOCTYPE html>");
        html.Should().Contain("Test Run");
        html.Should().Contain("Avg FPS");
        html.Should().Contain("Janky Frames");
        html.Should().Contain("3 samples");
        html.Should().Contain("<table>");
        html.Should().Contain("53.0", "avg of 60/59/40"); // invariant culture formatting
    }

    [Fact]
    public void Render_EscapesHtmlInTitle()
    {
        var html = ProfilerReportHtml.Render("<script>alert(1)</script>", Snapshots());
        html.Should().NotContain("<script>alert");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Render_EmptySnapshots_NoTableRows()
    {
        var html = ProfilerReportHtml.Render("Empty", Array.Empty<ProfilerSnapshot>());
        html.Should().Contain("0 samples");
        html.Should().Contain("n/a");
    }
}
