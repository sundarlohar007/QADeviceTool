using System.Diagnostics;

namespace LogPro.Tests.Cli;

/// <summary>
/// End-to-end CLI smoke tests against a fake adb (LogPro.TestFakes) — no hardware required.
/// PATH is overridden for the child process so the fake adb.exe shadows any real adb.
/// </summary>
public class CliSmokeTests
{
    /// <summary>
    /// Stages an isolated CLI home: the CLI + engine dlls plus the fake adb.exe.
    /// No tools/ dir inside → ToolResolver falls back to PATH → fake adb (which is also
    /// the child's working dir, so bare "adb" resolves there regardless of PATH order).
    /// </summary>
    private static string StageCliHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"LogProCliHome_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var bin = AppContext.BaseDirectory;
        foreach (var file in new[]
        {
            "logpro-cli.dll", "logpro-cli.deps.json", "logpro-cli.runtimeconfig.json",
            "LogPro.Core.dll", "NLog.dll", "CommunityToolkit.Mvvm.dll",
            "adb.exe", "adb.dll", "adb.runtimeconfig.json", "adb.deps.json"
        })
        {
            var src = Path.Combine(bin, file);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dir, file));
        }
        if (!File.Exists(Path.Combine(dir, "logpro-cli.dll")))
            throw new FileNotFoundException("logpro-cli.dll not found in test output — build LogPro.Cli first");
        return dir;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string cliHome, string arguments, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{Path.Combine(cliHome, "logpro-cli.dll")}\" {arguments}",
            WorkingDirectory = cliHome,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["PATH"] = cliHome + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw new TimeoutException($"logpro {arguments} did not exit within {timeoutMs}ms");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    [Fact]
    public async Task Devices_ListsFakeDevice()
    {
        var home = StageCliHome();
        try
        {
            var (exit, stdout, _) = await RunCliAsync(home, "devices");
            exit.Should().Be(0, because: $"devices should succeed; output: {stdout}");
            stdout.Should().Contain("FAKE01");
            stdout.Should().Contain("Android");
        }
        finally { try { Directory.Delete(home, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Profile_WritesPerfReport()
    {
        var home = StageCliHome();
        var outDir = Path.Combine(Path.GetTempPath(), $"LogProProfileTest_{Guid.NewGuid():N}");
        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(home, $"profile --serial FAKE01 --seconds 3 --package fakegame --out \"{outDir}\"", timeoutMs: 120000);
            exit.Should().Be(0, because: $"profile should succeed; stdout: {stdout} stderr: {stderr}");

            var json = Path.Combine(outDir, "profile-report.json");
            File.Exists(json).Should().BeTrue("profile-report.json must be written");

            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(json));
            var sampleCount = doc.RootElement.GetProperty("SampleCount").GetInt32();
            sampleCount.Should().BeGreaterThanOrEqualTo(2, "at least 2 samples for 3s @1s interval");

            var summary = doc.RootElement.GetProperty("Summary");
            summary.GetProperty("AvgFps").GetDouble().Should().BeGreaterThan(30.0, "fake layer streams ~60fps");
            summary.GetProperty("JankyFrames").GetInt32().Should().BeGreaterThan(0, "fake layer injects jank frames");

            File.Exists(Path.Combine(outDir, "profile.csv")).Should().BeTrue();
            stdout.Should().Contain("Avg FPS");
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { /* best effort */ }
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Capture_WritesLogFile()
    {
        var home = StageCliHome();
        var outDir = Path.Combine(Path.GetTempPath(), $"LogProCliTest_{Guid.NewGuid():N}");
        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(home, $"capture --serial FAKE01 --seconds 2 --out \"{outDir}\"", timeoutMs: 90000);
            exit.Should().Be(0, because: $"capture should succeed; stdout: {stdout} stderr: {stderr}");

            var logs = Directory.GetFiles(outDir, "*_log.txt", SearchOption.AllDirectories);
            logs.Should().HaveCount(1, "one session log file should be written");

            var lines = await File.ReadAllLinesAsync(logs[0]);
            lines.Length.Should().BeGreaterThan(5, "fake logcat streams ~50 lines/sec");
            lines[0].Should().Contain("FakeGame");
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { /* best effort */ }
            try { Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }
}
