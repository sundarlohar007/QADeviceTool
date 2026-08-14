using System.Globalization;
using System.Text.RegularExpressions;

namespace LogPro.Services.Profiling;

/// <summary>
/// Parsers for the engine-agnostic Android performance surfaces (§12.1, §13.2).
/// Every parser is pure — validated against synthetic dumpsys output in tests.
/// </summary>
public static class AndroidDumpsysParsers
{
    /// <summary>
    /// Parses `dumpsys SurfaceFlinger --latency &lt;layer&gt;`.
    /// Line 1 = refresh period (ns). Each following line: "appTs\tsfTs\tpresentTs".
    /// Present timestamps of 0 are pending frames and are skipped.
    /// Frame time = delta between consecutive present timestamps.
    /// </summary>
    public static SurfaceFlingerLatencyResult ParseSurfaceFlingerLatency(string output)
    {
        var frames = new List<FrameSample>();
        double refreshMs = 16.67;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (i == 0 && long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var periodNs) && periodNs > 0)
            {
                refreshMs = periodNs / 1_000_000.0;
                continue;
            }

            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var presentNs) || presentNs <= 0)
                continue;

            var prev = frames.LastOrDefault();
            var frameTimeMs = prev is null ? 0.0 : (presentNs - prev.PresentTimestampNs) / 1_000_000.0;
            if (frameTimeMs > 0 && frameTimeMs < 1000) // sanity: reject gaps from buffer misses
                frames.Add(new FrameSample(presentNs, frameTimeMs));
            else if (prev is null)
                frames.Add(new FrameSample(presentNs, 0.0));
        }

        return new SurfaceFlingerLatencyResult { RefreshPeriodMs = refreshMs, Frames = frames };
    }

    /// <summary>Computes FPS + jank from decoded SurfaceFlinger frames (16.67 ms budget default).</summary>
    public static (double? Fps, double? FrameTimeP90Ms, double? FrameTimeP95Ms, int? JankyFrames, int? TotalFrames)
        SummarizeFrames(IReadOnlyList<FrameSample> frames, double refreshPeriodMs = 16.67)
    {
        if (frames.Count == 0) return (null, null, null, null, null);

        var times = frames.Where(f => f.FrameTimeMs > 0).Select(f => f.FrameTimeMs).OrderBy(t => t).ToList();
        var count = times.Count;
        if (count == 0) return (null, null, null, null, frames.Count);

        // FPS from present timestamps: count / span
        var spanMs = (frames[^1].PresentTimestampNs - frames[0].PresentTimestampNs) / 1_000_000.0;
        var fps = spanMs > 0 ? count * 1000.0 / spanMs : 0.0;

        var budgetMs = refreshPeriodMs * 1.05; // ~5% tolerance over vsync
        var janky = times.Count(t => t > budgetMs);

        double Percentile(double p)
        {
            var idx = (int)Math.Ceiling(p * count) - 1;
            return times[Math.Clamp(idx, 0, count - 1)];
        }

        return (fps, Percentile(0.90), Percentile(0.95), janky, count);
    }

    /// <summary>
    /// Parses `dumpsys gfxinfo &lt;pkg&gt; framestats`. Returns frame durations from the
    /// INTENDED_VSYNC → FRAME_COMPLETED column pair, or an empty list when unavailable
    /// (SurfaceView-rendered games emit no framestats rows — the §12.1 caveat).
    /// </summary>
    public static List<double> ParseGfxInfoFrameStats(string output)
    {
        var durations = new List<double>();
        var inData = false;
        int intendedIdx = -1, completedIdx = -1;

        foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.StartsWith("---PROFILEDATA---", StringComparison.Ordinal)) { inData = true; continue; }
            if (!inData) continue;
            if (line.StartsWith("---", StringComparison.Ordinal)) break;

            if (intendedIdx < 0)
            {
                var headers = line.Split(',');
                for (var i = 0; i < headers.Length; i++)
                {
                    var normalized = headers[i].Trim().Replace("_", "");
                    if (normalized.Equals("IntendedVsync", StringComparison.OrdinalIgnoreCase)) intendedIdx = i;
                    else if (normalized.Equals("FrameCompleted", StringComparison.OrdinalIgnoreCase)) completedIdx = i;
                }
                continue;
            }

            var cols = line.Split(',');
            if (completedIdx >= cols.Length || intendedIdx >= cols.Length) continue;
            if (!long.TryParse(cols[intendedIdx], out var intended) || !long.TryParse(cols[completedIdx], out var completed))
                continue;
            var ms = (completed - intended) / 1_000_000.0;
            if (ms > 0 && ms < 1000) durations.Add(ms);
        }

        return durations;
    }

    private static readonly Regex CpuPackageLine = new(
        @"^\s*(?<total>[0-9.]+)%\s+\d+/(?<pkg>[\w.]+):\s",
        RegexOptions.Compiled);

    /// <summary>Parses `dumpsys cpuinfo` and returns the total % of the line whose package matches.</summary>
    public static double? ParseCpuPercent(string cpuinfo, string? packageSubstring = null)
    {
        foreach (var raw in cpuinfo.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = CpuPackageLine.Match(raw);
            if (!m.Success) continue;
            if (packageSubstring != null && !m.Groups["pkg"].Value.Contains(packageSubstring, StringComparison.OrdinalIgnoreCase))
                continue;
            if (double.TryParse(m.Groups["total"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return pct;
        }
        return null;
    }

    /// <summary>Parses `dumpsys meminfo &lt;pkg&gt;` TOTAL PSS/RSS rows.</summary>
    public static (int? PssKb, int? RssKb) ParseMemInfoTotals(string meminfo)
    {
        int? pss = null, rss = null;
        foreach (var raw in meminfo.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("TOTAL PSS:", StringComparison.OrdinalIgnoreCase))
                pss = ParseKb(line);
            else if (line.StartsWith("TOTAL RSS:", StringComparison.OrdinalIgnoreCase))
                rss = ParseKb(line);
        }
        return (pss, rss);

        static int? ParseKb(string line)
        {
            var m = Regex.Match(line, @"(?<n>\d+)\s*K?\b");
            return m.Success && int.TryParse(m.Groups["n"].Value, out var v) ? v : null;
        }
    }

    /// <summary>Parses the "Thermal status: N" line from `dumpsys thermalservice`.</summary>
    public static int? ParseThermalStatus(string thermalservice)
    {
        var m = Regex.Match(thermalservice, @"Thermal status:\s*(?<s>\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups["s"].Value, out var v) ? v : null;
    }

    /// <summary>Parses battery level from `dumpsys battery` ("level: 87").</summary>
    public static int? ParseBatteryLevel(string battery)
    {
        var m = Regex.Match(battery, @"level:\s*(?<l>\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups["l"].Value, out var v) ? v : null;
    }
}
