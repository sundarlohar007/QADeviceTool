using System.Text.Json;
using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.Tests.Services;

public class SessionServiceSessionWorkflowTests
{
    private static SessionService CreateService(string root) => new(new AdbService(), new IosService())
    {
        SessionsRootDirectory = root
    };

    [Fact]
    public async Task SaveLogCopyAsync_CreatesSeparateCopyAndPreservesCaptureFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProSave_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var service = CreateService(root);
            var session = service.CreateSession(new DeviceInfo { Serial = "unit-1", Platform = DevicePlatform.Android });
            await File.WriteAllTextAsync(session.LogFilePath, "first\nsecond\n");

            var copy = await service.SaveLogCopyAsync(session);

            copy.Should().NotBe(session.LogFilePath);
            (await File.ReadAllTextAsync(copy)).Should().Be("first\nsecond\n");
            (await File.ReadAllTextAsync(session.LogFilePath)).Should().Be("first\nsecond\n");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GetSavedSessions_UsesMetadataAndMainLogRatherThanAppOrSavedCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProArchive_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var service = CreateService(root);
            var session = service.CreateSession(new DeviceInfo
            {
                Serial = "unit-2",
                Name = "Device",
                Platform = DevicePlatform.Android
            }, "Investigation");
            await File.WriteAllTextAsync(session.LogFilePath, "main");
            await File.WriteAllTextAsync(Path.Combine(session.SessionDirectory, "Android_x_app_log.txt"), "app");
            await File.WriteAllTextAsync(Path.Combine(session.SessionDirectory, "saved_log_20260930.txt"), "copy");

            var archived = service.GetSavedSessions().Single();

            archived.LogFilePath.Should().Be(session.LogFilePath);
            archived.Name.Should().Be(session.Name);
            archived.DeviceId.Should().Be(SecurityHelper.HashSerial("unit-2"));
            archived.Platform.Should().Be(DevicePlatform.Android);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CsvAndJsonExports_ParseThreadtimeSeverityAndTimestamp()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProExport_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var service = CreateService(root);
            var session = service.CreateSession(new DeviceInfo { Serial = "unit-3", Platform = DevicePlatform.Android });
            await File.WriteAllTextAsync(session.LogFilePath,
                "09-30 12:34:56.789  1234  5678 E Example: failed\n");
            var csv = Path.Combine(root, "out.csv");
            var json = Path.Combine(root, "out.json");

            (await service.ExportToCsvAsync(session, csv)).Should().BeTrue();
            (await service.ExportToJsonAsync(session, json)).Should().BeTrue();

            (await File.ReadAllTextAsync(csv)).Should().Contain("09-30 12:34:56.789").And.Contain("Error");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(json));
            document.RootElement[0].GetProperty("Level").GetString().Should().Be("Error");
            document.RootElement[0].GetProperty("Timestamp").GetString().Should().Be("09-30 12:34:56.789");
        }
        finally { Directory.Delete(root, true); }
    }
}
