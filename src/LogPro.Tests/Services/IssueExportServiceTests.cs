using LogPro.Models;
using LogPro.Services;

namespace LogPro.Tests.Services;

public class IssueExportServiceTests
{
    [Fact]
    public async Task Export_WritesRedactedBundle()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"LogProIssue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var logFile = Path.Combine(outDir, "src_log.txt");
        await File.WriteAllTextAsync(logFile, "logcat evidence line\n");

        try
        {
            var bundle = await IssueExportService.ExportAsync(new IssueExportRequest
            {
                Device = new DeviceInfo { Serial = "RF8M1234ABCD", Model = "Pixel 7", OsVersion = "Android 14", Platform = DevicePlatform.Android },
                Title = "Frame drop in level 3",
                SessionLogFilePath = logFile,
                OutputDirectory = outDir
            });

            Directory.Exists(bundle.DirectoryPath).Should().BeTrue();
            File.Exists(bundle.MarkdownPath).Should().BeTrue();

            var md = await File.ReadAllTextAsync(bundle.MarkdownPath);
            md.Should().Contain("Frame drop in level 3");
            md.Should().Contain("Pixel 7");
            md.Should().Contain("session_log.txt");
            md.Should().Contain("never transmitted");

            var all = string.Join('\n', await Task.WhenAll(bundle.Files.Select(f => File.ReadAllTextAsync(f))));
            all.Should().NotContain("RF8M1234ABCD", "raw serial must never appear in the bundle");

            bundle.Files.Should().Contain(f => Path.GetFileName(f) == "session_log.txt");
        }
        finally { Directory.Delete(outDir, true); }
    }

    [Fact]
    public async Task Export_NoDevice_StillWorks()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"LogProIssue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            var bundle = await IssueExportService.ExportAsync(new IssueExportRequest
            {
                Title = "No-device bundle",
                OutputDirectory = outDir
            });
            var md = await File.ReadAllTextAsync(bundle.MarkdownPath);
            md.Should().Contain("nodevice");
        }
        finally { Directory.Delete(outDir, true); }
    }

    [Fact]
    public async Task Export_CopiesOnlyExistingAttachments()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"LogProIssue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var real = Path.Combine(outDir, "shot.png");
        await File.WriteAllTextAsync(real, "png-bytes");
        try
        {
            var bundle = await IssueExportService.ExportAsync(new IssueExportRequest
            {
                OutputDirectory = outDir,
                Attachments = new[] { real, Path.Combine(outDir, "missing.png") }
            });
            bundle.Files.Should().Contain(f => Path.GetFileName(f) == "shot.png");
            bundle.Files.Should().NotContain(f => Path.GetFileName(f) == "missing.png");
        }
        finally { Directory.Delete(outDir, true); }
    }
}
