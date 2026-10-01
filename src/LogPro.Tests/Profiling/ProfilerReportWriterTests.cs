using System.Text.Json;
using LogPro.Services.Profiling;

namespace LogPro.Tests.Profiling;

public class ProfilerReportWriterTests
{
    [Fact]
    public async Task Export_PreservesMissingMetricsAndTimelineMarkers()
    {
        var root = Path.Combine(Path.GetTempPath(), "LogProPerfExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var at = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
            var samples = new[] { new ProfilerSnapshot { Timestamp = at, CpuPercent = 10, SampleDurationMs = 42 } };
            var markers = new[] { new ProfilerMarker(at, "Manual marker") };
            var json = Path.Combine(root, "report.json");
            var csv = Path.Combine(root, "report.csv");
            await ProfilerReportWriter.WriteJsonAsync(samples, json, markers);
            await ProfilerReportWriter.WriteCsvAsync(samples, csv, markers);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(json));
            var summary = doc.RootElement.GetProperty("Summary");
            summary.GetProperty("Verdict").GetString().Should().Be("INSUFFICIENT DATA");
            summary.GetProperty("AvgFps").ValueKind.Should().Be(JsonValueKind.Null);
            doc.RootElement.GetProperty("Markers").GetArrayLength().Should().Be(1);
            var csvText = await File.ReadAllTextAsync(csv);
            csvText.Should().Contain("Manual marker");
            csvText.Should().Contain("SampleDurationMs");
            csvText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split(',').Length)
                .Should().OnlyContain(count => count == 15, "sample and marker rows must align with the header");
        }
        finally { Directory.Delete(root, true); }
    }
}
