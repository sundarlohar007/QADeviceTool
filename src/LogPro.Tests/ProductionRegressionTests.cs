using LogPro.Helpers;
using LogPro.Services.Profiling;

namespace LogPro.Tests;

public class ProductionRegressionTests
{
    [Fact]
    public void SurfaceFlinger_UsesActualPresentationAndIgnoresPendingFrames()
    {
        var output = "16666666\n1\t100000000\t90000000\n2\t116666666\t95000000\n3\t9223372036854775807\t120000000\n";
        var frames = AndroidDumpsysParsers.ParseSurfaceFlingerLatency(output).Frames;
        frames.Should().HaveCount(2);
        frames[0].PresentTimestampNs.Should().Be(100000000);
        frames[1].FrameTimeMs.Should().BeApproximately(16.666666, 0.00001);
    }

    [Theory]
    [InlineData("{\"token\":\"synthetic-secret\"}", "synthetic-secret")]
    [InlineData("{\"password\": \"a password with spaces\"}", "a password with spaces")]
    [InlineData("serial=00008020-0011223344556677", "00008020-0011223344556677")]
    public void Redaction_CoversStructuredCredentialsAndModernDeviceIds(string input, string secret)
        => SecurityHelper.RedactSensitiveText(input).Should().NotContain(secret);

    [Fact]
    public async Task IssueBundles_RedactUppercaseAttachments_AndAvoidReservedNameCollisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "LogProBundleRegression_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "issue.md");
            var second = Path.Combine(root, "DETAILS.JSON");
            await File.WriteAllTextAsync(first, "source evidence synthetic-unit-id");
            await File.WriteAllTextAsync(second, """{"password":"synthetic private value"}""");
            var request = new LogPro.Services.IssueExportRequest
            {
                OutputDirectory = root,
                Attachments = new[] { first, second },
                Device = new LogPro.Models.DeviceInfo { Serial = "synthetic-unit-id" }
            };
            var a = await LogPro.Services.IssueExportService.ExportAsync(request);
            var b = await LogPro.Services.IssueExportService.ExportAsync(request);
            a.DirectoryPath.Should().NotBe(b.DirectoryPath);
            a.Files.Should().HaveCount(4);
            var combined = string.Join("\n", await Task.WhenAll(a.Files.Select(f => File.ReadAllTextAsync(f))));
            combined.Should().Contain("source evidence").And.Contain("Steps to reproduce")
                .And.NotContain("synthetic private value").And.NotContain("synthetic-unit-id");
        }
        finally { Directory.Delete(root, true); }
    }
}
