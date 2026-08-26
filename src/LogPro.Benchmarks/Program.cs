using BenchmarkDotNet.Attributes;
using LogPro.Services.Profiling;

namespace LogPro.Benchmarks;

/// <summary>§9.4 hot-path micro-benchmarks. Run: dotnet run -c Release --project src/LogPro.Benchmarks</summary>
[MemoryDiagnoser]
public class HotPathBenchmarks
{
    private string _latencyOutput = string.Empty;
    private string _cpuinfo = string.Empty;
    private string _meminfo = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new System.Text.StringBuilder("16666666\n");
        long present = 10_000_000_000L;
        for (var i = 0; i < 500; i++)
        {
            present += 16_666_666L;
            if (i % 10 == 0) present += 20_000_000L;
            sb.AppendLine($"{i * 16_666_666:D14}\t{i * 16_666_666 + 2_000_000:D14}\t{present:D14}");
        }
        _latencyOutput = sb.ToString();

        _cpuinfo = "CPU usage from 1000ms to 0ms ago:\n" +
                   "  38% 2345/com.fakegame: 25% user + 13% kernel / faults: 42 minor\n" +
                   "  9% 999/system: 5% user + 4% kernel\n";
        _meminfo = "     TOTAL PSS:    384000\n     TOTAL RSS:    462000\n       OTHER 120\n";
    }

    [Benchmark]
    public SurfaceFlingerLatencyResult ParseSurfaceFlingerLatency()
        => AndroidDumpsysParsers.ParseSurfaceFlingerLatency(_latencyOutput);

    [Benchmark]
    public (double? Fps, double? P90, double? P95, int? Jank, int? Total) SummarizeFrames()
    {
        var result = AndroidDumpsysParsers.ParseSurfaceFlingerLatency(_latencyOutput);
        return AndroidDumpsysParsers.SummarizeFrames(result.Frames, result.RefreshPeriodMs);
    }

    [Benchmark]
    public double? ParseCpuPercent() => AndroidDumpsysParsers.ParseCpuPercent(_cpuinfo, "fakegame");

    [Benchmark]
    public (int? Pss, int? Rss) ParseMemInfoTotals() => AndroidDumpsysParsers.ParseMemInfoTotals(_meminfo);

    [Benchmark]
    public string HashSerial() => LogPro.Helpers.SecurityHelper.HashSerial("RF8M1234ABCD");
}

public static class EntryPoint
{
    public static void Main(string[] args)
        => BenchmarkDotNet.Running.BenchmarkRunner.Run<LogPro.Benchmarks.HotPathBenchmarks>(
            args: args);
}
