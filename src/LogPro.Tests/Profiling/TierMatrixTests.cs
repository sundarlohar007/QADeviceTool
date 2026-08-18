using LogPro.Services.Profiling;
using Moq;

namespace LogPro.Tests.Profiling;

public class TierMatrixTests
{
    private static Mock<LogPro.Services.IAdbService> CreateFakeAdb(double fastFps, double slowFps)
    {
        var mock = new Mock<LogPro.Services.IAdbService>();
        mock.Setup(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string serial, string command) =>
            {
                if (command.Contains("SurfaceFlinger --list"))
                    return "SurfaceView[com.fakegame/com.fakegame.MainActivity](BLAST)#0\n";
                if (command.Contains("SurfaceFlinger --latency"))
                    return Latency(serial == "SLOW01" ? slowFps : fastFps);
                if (command.Contains("cpuinfo"))
                    return "  38% 2345/com.fakegame: 25% user + 13% kernel\n";
                if (command.Contains("meminfo"))
                    return "     TOTAL PSS:    384000\n";
                if (command.Contains("thermalservice"))
                    return "Thermal status: 0\n";
                if (command.Contains("battery"))
                    return "level: 87\n";
                return string.Empty;
            });
        return mock;
    }

    private static string Latency(double fps)
    {
        var ns = (long)(1_000_000_000.0 / fps);
        var sb = new System.Text.StringBuilder("16666666\n");
        long present = 10_000_000_000L;
        for (var i = 0; i < 120; i++)
        {
            present += ns;
            sb.AppendLine($"{i * ns:D14}\t{i * ns + 2_000_000:D14}\t{present:D14}");
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Compare_DifferentiatesFastAndSlowDevices()
    {
        var adb = CreateFakeAdb(fastFps: 60, slowFps: 30);
        var profiles = new[]
        {
            new DeviceTierProfile { Serial = "FAST01", Label = "Flagship", Chipset = "Tensor G2", RamMb = 8192 },
            new DeviceTierProfile { Serial = "SLOW01", Label = "Budget", Chipset = "Snapdragon 480", RamMb = 4096 }
        };

        var results = await TierMatrix.CompareAsync(adb.Object, profiles, "com.fakegame", TimeSpan.FromSeconds(2));

        results.Should().HaveCount(2);
        var fast = results.Single(r => r.Profile.Serial == "FAST01");
        var slow = results.Single(r => r.Profile.Serial == "SLOW01");
        fast.AvgFps!.Value.Should().BeGreaterThan(slow.AvgFps!.Value, "flagship tier must beat budget tier");
        fast.AvgFps.Value.Should().BeApproximately(60, 5);
        slow.AvgFps.Value.Should().BeApproximately(30, 5);
        fast.Profile.Label.Should().Be("Flagship");
    }

    [Fact]
    public async Task WriteJson_RoundTripsDevices()
    {
        var adb = CreateFakeAdb(60, 30);
        var profiles = new[] { new DeviceTierProfile { Serial = "FAST01", Label = "L1" } };
        var results = await TierMatrix.CompareAsync(adb.Object, profiles, null, TimeSpan.FromSeconds(1));

        var path = Path.Combine(Path.GetTempPath(), $"tier_{Guid.NewGuid():N}.json");
        try
        {
            await TierMatrix.WriteJsonAsync(results, path);
            var json = await File.ReadAllTextAsync(path);
            json.Should().Contain("FAST01");
            json.Should().Contain("L1");
            json.Should().Contain("AvgFps");
        }
        finally { File.Delete(path); }
    }
}
