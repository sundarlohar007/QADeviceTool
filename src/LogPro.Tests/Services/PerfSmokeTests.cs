using System.Diagnostics;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.Tests.Services;

/// <summary>
/// §9.4 perf smoke gate: 1M-line synthetic log through the hot paths (parse, CSV/JSON export, tail-read).
/// Budgets are deliberately loose — this gates against pathological regressions (O(n²), full-buffering),
/// not micro-perf. Run/filter via: dotnet test --filter "Category=Perf"
/// </summary>
[Trait("Category", "Perf")]
[Collection("HeavyE2E")]
public class PerfSmokeTests : IDisposable
{
    private const int LineCount = 1_000_000;
    private readonly string _dir;
    private readonly string _logFile;
    private readonly SessionService _svc;
    private readonly LogSession _session;

    public PerfSmokeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"LogProPerf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _logFile = Path.Combine(_dir, "synthetic_log.txt");

        // logcat -v threadtime-ish lines, ~70 bytes each → ~70 MB file
        using (var sw = new StreamWriter(_logFile))
        {
            var batch = new System.Text.StringBuilder(700_000);
            for (var i = 0; i < LineCount; i++)
            {
                batch.Append("08-11 14:23:45.123  1234  5678 I/GameEngine: frame event ").Append(i).Append(" payload\n");
                if (i % 10_000 == 9_999) { sw.Write(batch); batch.Clear(); }
            }
            if (batch.Length > 0) sw.Write(batch);
        }

        _svc = new SessionService(new AdbService(), new IosService());
        _session = new LogSession { LogFilePath = _logFile, SessionDirectory = _dir };
    }

    [Fact]
    public async Task CsvExport_1M_Lines_StreamsWithinBudget()
    {
        var outPath = Path.Combine(_dir, "export.csv");
        var sw = Stopwatch.StartNew();

        var ok = await _svc.ExportToCsvAsync(_session, outPath);

        sw.Stop();
        ok.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(45), "CSV export must stream, not buffer");

        var rows = 0;
        using var reader = new StreamReader(outPath);
        while (await reader.ReadLineAsync() != null) rows++;
        rows.Should().Be(LineCount + 1, "1M data rows + 1 header");
    }

    [Fact]
    public async Task JsonExport_1M_Lines_StreamsWithinBudget()
    {
        var outPath = Path.Combine(_dir, "export.json");
        var sw = Stopwatch.StartNew();

        var ok = await _svc.ExportToJsonAsync(_session, outPath);

        sw.Stop();
        ok.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "JSON export must stream via Utf8JsonWriter");

        using var reader = new StreamReader(outPath);
        var first = reader.ReadLine();
        first.Should().StartWith("[", "output must be a JSON array");
        new FileInfo(outPath).Length.Should().BeGreaterThan(LineCount * 20); // sanity: content actually written
    }

    [Fact]
    public async Task TailRead_1M_Lines_BoundedAndFast()
    {
        var sw = Stopwatch.StartNew();

        var content = await _svc.ReadLogContentAsync(_session, maxLines: 500);

        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "tail-read must not load the whole file eagerly into a list");

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(500);
        lines[^1].Should().EndWith("frame event 999999 payload");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
