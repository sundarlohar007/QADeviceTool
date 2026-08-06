using LogPro.Models;
using LogPro.Services;

namespace LogPro.Tests.Services;

public class SessionServiceReadLogTests
{
    private static readonly LogPro.Services.IAdbService _adb = new LogPro.Services.AdbService();
    private static readonly LogPro.Services.IIosService _ios = new LogPro.Services.IosService();

    [Fact]
    public async Task ReadLogContentAsync_TailReadsWithoutLoadingWholeFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"LogProTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "big_log.txt");
        try
        {
            var lineCount = 500_000; // ~10 MB of synthetic lines
            using (var sw = new StreamWriter(file))
                for (var i = 0; i < lineCount; i++) sw.WriteLine($"line {i} payload padding");

            var session = new LogSession { LogFilePath = file, SessionDirectory = dir };
            var svc = new SessionService(_adb, _ios);
            var content = await svc.ReadLogContentAsync(session, maxLines: 500);

            var lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            lines.Length.Should().Be(500);
            lines[^1].Should().EndWith("499999 payload padding");
            lines[0].Should().EndWith("499500 payload padding");
        }
        finally { Directory.Delete(dir, true); }
    }
}
