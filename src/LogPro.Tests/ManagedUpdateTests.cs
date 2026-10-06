using System.IO;
using System.IO.Compression;
using LogPro.Services;

namespace LogPro.Tests;

public class ManagedUpdateTests
{
    [Theory]
    [InlineData("2.0", "3.0", "version does not match")]
    [InlineData("2.0", "2.0", "health check failed")]
    public async Task RejectedPackage_PreservesWorkingToolAndManifest(string packageVersion, string expectedVersion, string error)
    {
        var root = Path.Combine(Path.GetTempPath(), "LogProUpdateTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tools", "adb"));
        try
        {
            var executable = Path.Combine(root, "tools", "adb", "adb.exe");
            await File.WriteAllTextAsync(executable, "original working binary");
            var manifest = Path.Combine(root, ToolManifest.DefaultFileName);
            await ToolManifest.WriteAsync(Path.Combine(root, "tools"), manifest);
            var originalManifest = await File.ReadAllTextAsync(manifest);
            var payload = Path.Combine(root, "payload", "adb");
            Directory.CreateDirectory(payload);
            await File.WriteAllTextAsync(Path.Combine(payload, "adb.exe"), "invalid executable");
            await File.WriteAllTextAsync(Path.Combine(payload, "tool-version.txt"), packageVersion);
            var archive = Path.Combine(root, "update.zip");
            ZipFile.CreateFromDirectory(Path.GetDirectoryName(payload)!, archive);
            using var updater = new UpdateService(Path.Combine(root, "tools"), root);
            var action = () => updater.InstallToolAsync(archive, "adb", expectedVersion, CancellationToken.None);
            await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*" + error + "*");
            (await File.ReadAllTextAsync(executable)).Should().Be("original working binary");
            (await File.ReadAllTextAsync(manifest)).Should().Be(originalManifest);
            (await ToolManifest.VerifyAsync(Path.Combine(root, "tools"), manifest)).IsHealthy.Should().BeTrue();
            Directory.GetDirectories(root, ".update_*").Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }
}
