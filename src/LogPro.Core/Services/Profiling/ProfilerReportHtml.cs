using System.Text;

namespace LogPro.Services.Profiling;

/// <summary>
/// Renders a self-contained HTML session report (§12.9) — no external assets or scripts.
/// </summary>
public static class ProfilerReportHtml
{
    public static string Render(string title, IReadOnlyList<ProfilerSnapshot> snapshots)
    {
        var summary = ProfilerReportWriter.Summarize(snapshots);
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>")
          .Append(Html(title)).Append("</title><style>")
          .Append("body{font-family:system-ui,sans-serif;background:#0e0e12;color:#e8eaed;margin:2rem}")
          .Append("h1{font-size:1.4rem;margin:0 0 .5rem}.muted{color:#9aa0a6;font-size:.8rem}")
          .Append(".cards{display:flex;flex-wrap:wrap;gap:.6rem;margin:1rem 0}")
          .Append(".card{background:#17171c;border-radius:10px;padding:1rem;min-width:150px}")
          .Append(".card .v{font-size:1.6rem;font-weight:700}.card .l{color:#9aa0a6;font-size:.75rem}")
          .Append(".bad{color:#f59e0b}.ok{color:#4ade80}.red{color:#ef4444}")
          .Append("table{border-collapse:collapse;width:100%;font-family:ui-monospace,monospace;font-size:.78rem}")
          .Append("td,th{padding:.35rem .7rem;border-bottom:1px solid #232329;text-align:left}")
          .Append("</style></head><body>");

        sb.Append("<h1>").Append(Html(title)).Append("</h1>");
        sb.Append("<div class=\"muted\">Generated ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")).Append(" UTC · ").Append(snapshots.Count).Append(" samples</div>");

        sb.Append("<div class=\"cards\">");
        Card("Avg FPS", Fmt(summary.AvgFps, "F1"), summary.AvgFps is null ? "muted" : (summary.AvgFps < 30 ? "red" : "ok"));
        Card("Min FPS", Fmt(summary.MinFps, "F1"), summary.MinFps is null ? "muted" : (summary.MinFps < 20 ? "red" : ""));
        Card("Janky Frames", summary.JankyFrames.ToString(), summary.JankyFrames > 0 ? "bad" : "ok");
        Card("Max CPU", summary.MaxCpuPercent is null ? "n/a" : $"{summary.MaxCpuPercent:F0}%", "");
        Card("Memory Growth", $"{summary.MemoryGrowthKb / 1024} MB", summary.MemoryGrowthKb > 0 ? "bad" : "ok");
        Card("Battery Drain", $"{summary.BatteryDrainPercent}%", summary.BatteryDrainPercent > 5 ? "bad" : "ok");
        Card("Max Thermal", summary.MaxThermalStatus.ToString(), summary.MaxThermalStatus >= 1 ? "bad" : "ok");
        Card("Verdict", summary.SlowSession ? "SLOW SESSION" : "OK", summary.SlowSession ? "bad" : "ok");
        sb.Append("</div>");

        sb.Append("<table><tr><th>Time</th><th>FPS</th><th>p90 ms</th><th>CPU %</th><th>PSS KB</th><th>Jank</th><th>Thermal</th><th>Battery</th></tr>");
        foreach (var s in snapshots)
        {
            sb.Append("<tr><td>").Append(s.Timestamp.ToString("HH:mm:ss")).Append("</td>")
              .Append("<td>").Append(Fmt(s.Fps, "F1")).Append("</td>")
              .Append("<td>").Append(Fmt(s.FrameTimeP90Ms, "F1")).Append("</td>")
              .Append("<td>").Append(Fmt(s.CpuPercent, "F0")).Append("</td>")
              .Append("<td>").Append(s.PssKb?.ToString() ?? "").Append("</td>")
              .Append("<td>").Append(s.JankyFrames?.ToString() ?? "").Append("</td>")
              .Append("<td>").Append(s.ThermalStatus?.ToString() ?? "").Append("</td>")
              .Append("<td>").Append(s.BatteryLevel?.ToString() ?? "").Append("</td></tr>");
        }
        sb.Append("</table></body></html>");
        return sb.ToString();

        void Card(string label, string value, string cssClass)
            => sb.Append("<div class=\"card\"><div class=\"l\">").Append(label)
                 .Append("</div><div class=\"v ").Append(cssClass).Append("\">").Append(value).Append("</div></div>");

        static string Fmt(double? v, string fmt)
            => v.HasValue ? v.Value.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) : "n/a";

        static string Html(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
