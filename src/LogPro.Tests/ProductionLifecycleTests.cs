using System.Diagnostics;
using System.Text;
using LogPro.Models;
using LogPro.Services;
using Moq;

namespace LogPro.Tests;

public class ProductionLifecycleTests
{
    [Fact]
    public async Task DelayedExitFromPreviousProcess_CannotStopReplacementOrOtherDevice()
    {
        using var prefs = new IsolatedPreferences();
        var processes = new List<Process>();
        var adb = new Mock<IAdbService>();
        adb.Setup(a => a.StartLogCaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()))
            .Returns(() =>
            {
                var process = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "adb.exe"), "logcat")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 })!;
                processes.Add(process);
                return Task.FromResult<Process?>(process);
            });
        var service = new SessionService(adb.Object, new Mock<IIosService>().Object) { SessionsRootDirectory = prefs.DirectoryPath };
        var first = service.CreateSession(new DeviceInfo { Serial = "A" });
        var second = service.CreateSession(new DeviceInfo { Serial = "B" });
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observeSecond = 0;
        service.CaptureBatchReceived += batch => { if (Volatile.Read(ref observeSecond) == 1 && batch.SessionId == second.Id) delivered.TrySetResult(); };
        try
        {
            (await service.StartCaptureAsync(first)).Should().BeTrue();
            service.StopCapture(first);
            await service.WaitForCaptureStopAsync(first);
            (await service.StartCaptureAsync(first)).Should().BeTrue();
            (await service.StartCaptureAsync(second)).Should().BeTrue();
            service.HandleCaptureExit(first, processes[0]);
            first.Status.Should().Be(SessionStatus.Capturing);
            first.StopReason.Should().BeEmpty();
            service.StopCapture(first);
            await service.WaitForCaptureStopAsync(first);
            Volatile.Write(ref observeSecond, 1);
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second.Status.Should().Be(SessionStatus.Capturing);
        }
        finally { service.StopAllCaptures(); }
    }

    [Theory]
    [InlineData(DevicePlatform.Android)]
    [InlineData(DevicePlatform.iOS)]
    public async Task Capture_PreservesUnicodeBeyond250Lines_AndDetectsCrashesWithoutAView(DevicePlatform platform)
    {
        using var prefs = new IsolatedPreferences();
        var adb = new Mock<IAdbService>();
        var ios = new Mock<IIosService>();
        Task<Process?> Start() => Task.FromResult(Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "adb.exe"), platform == DevicePlatform.iOS ? "--production-stream-ios" : "--production-stream")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 }));
        adb.Setup(a => a.StartLogCaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>())).Returns(Start);
        ios.Setup(a => a.StartLogCaptureAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Start);
        var service = new SessionService(adb.Object, ios.Object) { SessionsRootDirectory = prefs.DirectoryPath };
        var session = service.CreateSession(new DeviceInfo { Serial = "synthetic", Platform = platform });
        try
        {
            (await service.StartCaptureAsync(session)).Should().BeTrue();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (session.LogLineCount < 2001) await Task.Delay(20, deadline.Token);
            var snapshot = service.GetLiveSnapshot(session.Id)!;
            snapshot.Should().HaveCount(2001);
            snapshot.Select(l => l.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
            service.StopCapture(session);
            await service.WaitForCaptureStopAsync(session);
            session.CaptureComplete.Should().BeTrue(session.CaptureError);
            var lines = await File.ReadAllLinesAsync(session.LogFilePath);
            lines.Should().HaveCount(2001);
            lines[1999].Should().Contain("2000 \u202f 日本語");
            session.Crashes.CrashCount.Should().BeGreaterThan(0);
            service.GetSavedSessions().Single().Crashes.CrashCount.Should().BeGreaterThan(0);
            (await service.StartCaptureAsync(session)).Should().BeTrue();
            using var restartDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (session.LogLineCount < 4003) await Task.Delay(20, restartDeadline.Token);
            service.StopCapture(session);
            await service.WaitForCaptureStopAsync(session);
            session.CaptureComplete.Should().BeTrue(session.CaptureError);
            (await File.ReadAllLinesAsync(session.LogFilePath)).LongLength.Should().Be(session.LogLineCount,
                "restart markers are persisted lines and must be included in the saved count");
        }
        finally { service.StopAllCaptures(); }
    }

    [Fact]
    public async Task ConcurrentStarts_ReserveDeviceBeforeLaunching()
    {
        using var prefs = new IsolatedPreferences();
        var adb = new Mock<IAdbService>();
        var pending = new TaskCompletionSource<Process?>();
        adb.Setup(a => a.StartLogCaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>())).Returns(pending.Task);
        var service = new SessionService(adb.Object, new Mock<IIosService>().Object) { SessionsRootDirectory = prefs.DirectoryPath };
        var device = new DeviceInfo { Serial = "same-device", Platform = DevicePlatform.Android };
        var first = service.StartCaptureAsync(service.CreateSession(device));
        (await service.StartCaptureAsync(service.CreateSession(device))).Should().BeFalse();
        var shutdown = Task.Run(service.StopAllCaptures);
        pending.SetResult(null);
        await shutdown;
        (await first).Should().BeFalse();
        adb.Verify(a => a.StartLogCaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()), Times.Once);
        (await service.StartCaptureAsync(service.CreateSession(device))).Should().BeFalse("shutdown must reject late starts");
    }

    [Theory]
    [InlineData("E/Tag(123): failed")]
    [InlineData("E/Tag: failed")]
    [InlineData("E(123): failed")]
    [InlineData("E(123:456): failed")]
    [InlineData("10-06 12:00:00.000 E/Tag(123): failed")]
    [InlineData("10-06 12:00:00.000 123 456 E Tag: failed")]
    [InlineData("[ 10-06 12:00:00.000 123: 456 E/Tag ]")]
    public void SupportedLogcatHeaders_PreserveSeverity(string line)
        => LogLineParser.Parse(line).Level.Should().Be(LogLevel.Error);

    [Fact]
    public void AppMetadata_UsesLocalizedLabelsAndDisplayVersions()
    {
        var apps = new[] { new AppItem { PackageId = "com.example.app", Name = "com.example.app" } };
        AdbService.ApplyAppLabels(apps, """[{"package":"com.example.app","label":"日本語 App","version":"2.1 beta"}]""").Should().BeTrue();
        apps[0].Name.Should().Be("日本語 App");
        apps[0].Version.Should().Be("2.1 beta");
    }
}
