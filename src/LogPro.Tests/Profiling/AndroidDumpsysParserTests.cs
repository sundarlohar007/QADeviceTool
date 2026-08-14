using LogPro.Services.Profiling;

namespace LogPro.Tests.Profiling;

public class SurfaceFlingerLatencyParserTests
{
    private static string LatencyOutput(double refreshNs, params (long a, long s, long p)[] frames)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(refreshNs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (a, s, p) in frames)
            sb.AppendLine($"{a}\t{s}\t{p}");
        return sb.ToString();
    }

    [Fact]
    public void Parse_DecodesFramesAndRefresh()
    {
        // 60Hz refresh; frames presented every ~16.7ms; one pending frame (0)
        var output = LatencyOutput(16_666_666,
            (1000, 1000, 100_000_000),
            (2000, 2000, 116_666_666),
            (3000, 3000, 133_333_333),
            (4000, 4000, 0));

        var result = AndroidDumpsysParsers.ParseSurfaceFlingerLatency(output);

        result.RefreshPeriodMs.Should().BeApproximately(16.67, 0.01);
        result.Frames.Should().HaveCount(3, "pending (0) frames are skipped");
        result.Frames[1].FrameTimeMs.Should().BeApproximately(16.67, 0.1);
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsEmpty()
    {
        var result = AndroidDumpsysParsers.ParseSurfaceFlingerLatency("");
        result.Frames.Should().BeEmpty();
        result.RefreshPeriodMs.Should().BeApproximately(16.67, 0.01);
    }

    [Fact]
    public void Summarize_ComputesFpsAndJank()
    {
        // 120 frames at 60fps + 10 janky frames at 40ms
        var frames = new List<FrameSample>();
        long t = 0;
        for (var i = 0; i < 120; i++) { t += 16_666_666; frames.Add(new FrameSample(t, 16.67)); }
        for (var i = 0; i < 10; i++) { t += 16_666_666; frames.Add(new FrameSample(t, 40.0)); }

        var (fps, p90, p95, janky, total) = AndroidDumpsysParsers.SummarizeFrames(frames);

        fps.Should().HaveValue();
        fps!.Value.Should().BeApproximately(60.0, 3.0);
        total.Should().Be(130);
        janky.Should().Be(10);
        p95.Should().HaveValue();
        p95!.Value.Should().BeApproximately(40.0, 1.0);
    }
}

public class GfxInfoFrameStatsParserTests
{
    private static string FrameStats(int frameCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Applications Graphics Acceleration Info:");
        sb.AppendLine("---PROFILEDATA---");
        sb.AppendLine("Flags,IntendedVsync,Vsync,OldestInputEvent,NewestInputEvent,HandleInputStart,AnimationStart,PerformTraversalsStart,DrawStart,SyncQueued,SyncStart,IssueDrawCommandsStart,SwapBuffers,FrameCompleted,DequeueBufferDuration,QueueBufferDuration,GpuCompleted");
        for (var i = 0; i < frameCount; i++)
        {
            var intended = 100_000_000L + i * 16_666_666L;
            var completed = intended + (i % 5 == 0 ? 33_000_000 : 16_000_000);
            sb.AppendLine($"0,{intended},0,0,0,0,0,0,0,0,0,0,0,{completed},0,0,0");
        }
        sb.AppendLine("---PROFILEDATA---");
        return sb.ToString();
    }

    [Fact]
    public void Parse_ExtractsFrameDurations()
    {
        var durations = AndroidDumpsysParsers.ParseGfxInfoFrameStats(FrameStats(10));
        durations.Should().HaveCount(10);
        durations[0].Should().BeApproximately(33.0, 1.0); // every 5th frame is janky
        durations[1].Should().BeApproximately(16.0, 1.0);
    }

    [Fact]
    public void Parse_NoProfileData_Empty()
    {
        AndroidDumpsysParsers.ParseGfxInfoFrameStats("no such data").Should().BeEmpty();
    }
}

public class CpuMemThermalBatteryParserTests
{
    [Fact]
    public void ParseCpuPercent_FindsPackage()
    {
        var cpuinfo = "Load: 1.2 / 0.9 / 0.5\n" +
                      "CPU usage from 1000ms to 0ms ago:\n" +
                      "  45% 2345/com.supercell.brawlstars: 30% user + 15% kernel / faults: 100 minor\n" +
                      "  12% 999/system: 8% user + 4% kernel\n";
        AndroidDumpsysParsers.ParseCpuPercent(cpuinfo, "brawlstars").Should().Be(45.0);
        AndroidDumpsysParsers.ParseCpuPercent(cpuinfo, "nonexistent").Should().BeNull();
    }

    [Fact]
    public void ParseMemInfoTotals_ExtractsTotals()
    {
        var meminfo = "     TOTAL PSS:    412345\n     TOTAL RSS:    512345\n       OTHER 100\n";
        var (pss, rss) = AndroidDumpsysParsers.ParseMemInfoTotals(meminfo);
        pss.Should().Be(412345);
        rss.Should().Be(512345);
    }

    [Fact]
    public void ParseThermalStatus_ExtractsStatus()
    {
        AndroidDumpsysParsers.ParseThermalStatus("IsStatusOverride: false\nThermal status: 1\n").Should().Be(1);
        AndroidDumpsysParsers.ParseThermalStatus("nothing").Should().BeNull();
    }

    [Fact]
    public void ParseBatteryLevel_ExtractsLevel()
    {
        AndroidDumpsysParsers.ParseBatteryLevel("AC powered: false\nstatus: 3\nlevel: 87\nscale: 100").Should().Be(87);
        AndroidDumpsysParsers.ParseBatteryLevel("empty").Should().BeNull();
    }
}
