using LogPro.Services;

namespace LogPro.Tests.Services;

public class ToolManifestTests
{
    private static string CreateToolTree()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"LogProTools_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "platform-tools"));
        Directory.CreateDirectory(Path.Combine(dir, "scrcpy"));
        File.WriteAllText(Path.Combine(dir, "platform-tools", "adb.exe"), "fake-adb-binary");
        File.WriteAllText(Path.Combine(dir, "scrcpy", "scrcpy.exe"), "fake-scrcpy-binary");
        return dir;
    }

    [Fact]
    public async Task WriteThenVerify_Healthy()
    {
        var dir = CreateToolTree();
        try
        {
            var manifest = Path.Combine(dir, ToolManifest.DefaultFileName);
            await ToolManifest.WriteAsync(dir, manifest);

            var result = await ToolManifest.VerifyAsync(dir, manifest);
            result.IsHealthy.Should().BeTrue();
            result.Ok.Should().HaveCount(2);
            result.Mismatched.Should().BeEmpty();
            result.Missing.Should().BeEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Verify_TamperedFile_IsMismatched()
    {
        var dir = CreateToolTree();
        try
        {
            var manifest = Path.Combine(dir, ToolManifest.DefaultFileName);
            await ToolManifest.WriteAsync(dir, manifest);

            File.WriteAllText(Path.Combine(dir, "platform-tools", "adb.exe"), "TAMPERED");

            var result = await ToolManifest.VerifyAsync(dir, manifest);
            result.IsHealthy.Should().BeFalse();
            result.Mismatched.Should().HaveCount(1);
            result.Mismatched[0].Path.Should().Be("platform-tools/adb.exe");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Verify_RemovedAndAddedFiles_Detected()
    {
        var dir = CreateToolTree();
        try
        {
            var manifest = Path.Combine(dir, ToolManifest.DefaultFileName);
            await ToolManifest.WriteAsync(dir, manifest);

            File.Delete(Path.Combine(dir, "scrcpy", "scrcpy.exe"));
            File.WriteAllText(Path.Combine(dir, "intruder.dll"), "new");

            var result = await ToolManifest.VerifyAsync(dir, manifest);
            result.IsHealthy.Should().BeFalse();
            result.Missing.Should().Contain("scrcpy/scrcpy.exe");
            result.Unexpected.Should().Contain("intruder.dll");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Verify_NoManifest_Unhealthy()
    {
        var dir = CreateToolTree();
        try
        {
            var result = await ToolManifest.VerifyAsync(dir, Path.Combine(dir, "nope.json"));
            result.IsHealthy.Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }
}
