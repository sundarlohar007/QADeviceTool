using System.IO.Compression;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.Tests.Services;

public class OfficialUpdateSourceTests
{
    [Fact]
    public void ScrcpyRelease_UsesOfficialArchiveAndDigest()
    {
        var digest = new string('a', 64);
        var json = $$"""
            {"tag_name":"v5.0.1","assets":[
              {"name":"scrcpy-win32-v5.0.1.zip","digest":"sha256:{{digest}}","browser_download_url":"https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win32-v5.0.1.zip"},
              {"name":"scrcpy-win64-v5.0.1.zip","digest":"sha256:{{digest}}","browser_download_url":"https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip"}]}
            """;
        var result = UpdateService.ParseScrcpyRelease(json, "3.3.4.1");
        result.LatestVersion.Should().Be("5.0.1.0");
        result.IsInstallable.Should().BeTrue();
        result.Sha256.Should().Be(digest);
        UpdateService.IsTrustedDownloadUrl(result).Should().BeTrue();
        UpdateService.IsTrustedDownloadUrl(new UpdateInfo
        {
            ToolName = "scrcpy",
            DownloadUrl = "https://github.com/attacker/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip"
        }).Should().BeFalse();
        var forged = json.Replace("/Genymobile/scrcpy/releases/download/", "/attacker/scrcpy/releases/download/");
        var parse = () => UpdateService.ParseScrcpyRelease(forged, "3.3.4.1");
        parse.Should().Throw<InvalidDataException>().WithMessage("*trusted URL*");
    }

    [Fact]
    public void Pymobiledevice3Release_ReportsNewVersionWithoutOfferingPythonWheelAsExecutable()
    {
        var result = UpdateService.ParsePymobiledevice3Release("""{"info":{"version":"9.13.0"}}""", "9.12.0.1");
        result.IsNewerAvailable.Should().BeTrue();
        result.IsInstallable.Should().BeFalse();
        result.ReleaseNotes.Should().Contain("tested, frozen");
    }

    [Fact]
    public void GoogleArchive_RequiresAdbAndRevisionBeforeInstallation()
    {
        var path = Path.Combine(Path.GetTempPath(), "logpro-google-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var stream = File.Create(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using (var properties = new StreamWriter(archive.CreateEntry("platform-tools/source.properties").Open()))
                    properties.WriteLine("Pkg.Revision = 37.0.2");
                archive.CreateEntry("platform-tools/adb.exe");
            }
            UpdateService.ReadAdbArchiveVersion(path).Should().Be(new Version(37, 0, 2));
            UpdateService.IsTrustedDownloadUrl(new UpdateInfo
            {
                ToolName = "adb",
                DownloadUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
            }).Should().BeTrue();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("adb", "platform-tools", "37.0.2.0", "37.0.3", "revision differs")]
    [InlineData("scrcpy", "scrcpy-win64-v5.0.1", "5.0.1.0", "", "health check failed")]
    public async Task InvalidOfficialArchive_DoesNotReplaceBundledTool(
        string tool, string archiveFolder, string expectedVersion, string revision, string error)
    {
        var root = Path.Combine(Path.GetTempPath(), "LogProOfficialUpdate_" + Guid.NewGuid().ToString("N"));
        var tools = Path.Combine(root, "tools");
        var current = Path.Combine(tools, tool);
        Directory.CreateDirectory(current);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(current, tool + ".exe"), "original executable");
            var manifest = Path.Combine(root, ToolManifest.DefaultFileName);
            await ToolManifest.WriteAsync(tools, manifest);
            var originalManifest = await File.ReadAllTextAsync(manifest);
            var archivePath = Path.Combine(root, "official.zip");
            using (var stream = File.Create(archivePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using var executable = new StreamWriter(archive.CreateEntry(archiveFolder + "/" + tool + ".exe").Open());
                executable.Write("invalid executable");
                executable.Dispose();
                if (tool == "adb")
                {
                    using var properties = new StreamWriter(archive.CreateEntry(archiveFolder + "/source.properties").Open());
                    properties.Write("Pkg.Revision = " + revision);
                }
            }
            using var updater = new UpdateService(tools, root);
            var action = () => updater.InstallToolAsync(archivePath, tool, expectedVersion, CancellationToken.None);
            await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*" + error + "*");
            (await File.ReadAllTextAsync(Path.Combine(current, tool + ".exe"))).Should().Be("original executable");
            (await File.ReadAllTextAsync(manifest)).Should().Be(originalManifest);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GoogleArchive_InstallsVerifiedAdbAndKeepsInventoryHelper()
    {
        var root = Path.Combine(Path.GetTempPath(), "LogProGoogleUpdate_" + Guid.NewGuid().ToString("N"));
        var tools = Path.Combine(root, "tools");
        var installed = Path.Combine(tools, "adb");
        Directory.CreateDirectory(installed);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(installed, "adb.exe"), "previous adb");
            await File.WriteAllTextAsync(Path.Combine(installed, "inventory.jar"), "LogPro inventory helper");
            var manifest = Path.Combine(root, ToolManifest.DefaultFileName);
            await ToolManifest.WriteAsync(tools, manifest);
            var archivePath = Path.Combine(root, "official.zip");
            using (var stream = File.Create(archivePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var name in new[]
                {
                    "adb.exe", "adb.dll", "adb.deps.json", "adb.runtimeconfig.json", "LogPro.Core.dll", "NLog.dll"
                })
                    archive.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, name), "platform-tools/" + name);
                using var properties = new StreamWriter(archive.CreateEntry("platform-tools/source.properties").Open());
                properties.WriteLine("Pkg.Revision = 37.0.2");
            }
            using var updater = new UpdateService(tools, root);
            await updater.InstallToolAsync(archivePath, "adb", "37.0.2.0", CancellationToken.None);
            (await File.ReadAllTextAsync(Path.Combine(installed, "inventory.jar"))).Should().Be("LogPro inventory helper");
            (await File.ReadAllTextAsync(Path.Combine(installed, "tool-version.txt"))).Should().Be("37.0.2");
            (await ToolManifest.VerifyAsync(tools, manifest)).IsHealthy.Should().BeTrue();
        }
        finally { Directory.Delete(root, true); }
    }
}
