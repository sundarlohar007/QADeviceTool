using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class SessionWorkflowTests
{
    private static (SessionViewModel Vm, Mock<ISessionService> Sessions) Create(params DeviceInfo[] devices)
    {
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(x => x.CurrentDevices).Returns(devices.ToList());
        var sessions = new Mock<ISessionService>();
        sessions.Setup(x => x.ActiveSessions).Returns(new List<LogSession>());
        sessions.Setup(x => x.GetSavedSessions()).Returns(new List<LogSession>());
        var vm = new SessionViewModel(sessions.Object, new Mock<IAdbService>().Object,
            new Mock<IIosService>().Object, monitor.Object, new ImmediateUiDispatcher());
        return (vm, sessions);
    }

    [Fact]
    public void DeviceRefresh_KeepsSelectedDeviceInRefreshedList()
    {
        var a = new DeviceInfo { Serial = "A", Platform = DevicePlatform.Android };
        var b = new DeviceInfo { Serial = "B", Platform = DevicePlatform.Android };
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(x => x.CurrentDevices).Returns(new List<DeviceInfo> { a, b });
        var sessions = new Mock<ISessionService>();
        sessions.Setup(x => x.ActiveSessions).Returns(new List<LogSession>());
        sessions.Setup(x => x.GetSavedSessions()).Returns(new List<LogSession>());
        using var vm = new SessionViewModel(sessions.Object, new Mock<IAdbService>().Object,
            new Mock<IIosService>().Object, monitor.Object, new ImmediateUiDispatcher());
        vm.SelectedDevice = b;
        var refreshed = new List<DeviceInfo>
        {
            new() { Serial = "A", Platform = DevicePlatform.Android },
            new() { Serial = "B", Platform = DevicePlatform.Android }
        };

        monitor.Raise(x => x.DevicesChanged += null, refreshed);

        vm.SelectedDevice.Should().BeSameAs(refreshed[1]);
    }

    [Fact]
    public async Task StartCapture_DoesNotReuseIdleSessionForAnotherDevice()
    {
        var a = new DeviceInfo { Serial = "A", Platform = DevicePlatform.Android };
        var b = new DeviceInfo { Serial = "B", Platform = DevicePlatform.Android };
        var (vm, sessions) = Create(a, b);
        using (vm)
        {
            var idle = new LogSession { DeviceSerial = "A", Status = SessionStatus.Idle };
            var replacement = new LogSession { DeviceSerial = "B", Status = SessionStatus.Idle };
            sessions.Setup(x => x.CreateSession(b, It.IsAny<string>())).Returns(replacement);
            sessions.Setup(x => x.StartCaptureAsync(replacement, It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()))
                .ReturnsAsync(true);
            vm.Sessions.Add(idle);
            vm.SelectedSession = idle;
            vm.SelectedDevice = b;

            await vm.StartCaptureCommand.ExecuteAsync(null);

            sessions.Verify(x => x.StartCaptureAsync(replacement, It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()), Times.Once);
            vm.SelectedSession.Should().BeSameAs(replacement);
        }
    }

    [Fact]
    public void PauseDisplay_KeepsCapturedLinesAndReplaysOnResume()
    {
        var a = new DeviceInfo { Serial = "A", Platform = DevicePlatform.Android };
        var (vm, sessions) = Create(a);
        using (vm)
        {
            var session = new LogSession { DeviceSerial = "A", Status = SessionStatus.Capturing };
            vm.SelectedSession = session;
            sessions.Raise(x => x.CaptureStarted += null, session);
            vm.TogglePauseCommand.Execute(null);

            sessions.Raise(x => x.LogBatchReceived += null, session.Id, "09-30 12:34:56.789  1234  5678 E Example: failed\n");

            vm.LogEntries.Should().HaveCount(1);
            vm.LogEntriesView.Should().BeEmpty();
            vm.TogglePauseCommand.Execute(null);
            vm.LogEntriesView.Should().HaveCount(1);
            vm.IsRawMode = false;
            vm.LogEntriesView[0].Message.Should().Be("failed");
            vm.LogEntriesView[0].Tag.Should().Be("Example");
        }
    }

    [Fact]
    public void StoppingOneSession_KeepsOtherDevicesLiveSubscription()
    {
        var (vm, sessions) = Create(
            new DeviceInfo { Serial = "A", Platform = DevicePlatform.Android },
            new DeviceInfo { Serial = "B", Platform = DevicePlatform.Android });
        using (vm)
        {
            var a = new LogSession { DeviceSerial = "A", Status = SessionStatus.Capturing };
            var b = new LogSession { DeviceSerial = "B", Status = SessionStatus.Capturing };
            sessions.Raise(x => x.CaptureStarted += null, a);
            sessions.Raise(x => x.CaptureStarted += null, b);
            vm.SelectedSession = a;
            vm.StopCaptureCommand.Execute(null);
            vm.SelectedSession = b;

            sessions.Raise(x => x.LogBatchReceived += null, b.Id, "line from B\n");

            vm.LogEntries.Should().ContainSingle(e => e.RawLine == "line from B");
        }
    }

    [Fact]
    public async Task SearchEntireSession_FindsLinesOutsideViewerTail()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProSearch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "capture_log.txt");
            await File.WriteAllTextAsync(file, "older important line\nrecent line\n");
            var (vm, sessions) = Create();
            using (vm)
            {
                sessions.Setup(x => x.ReadLogContentAsync(It.IsAny<LogSession>(), It.IsAny<int>()))
                    .ReturnsAsync("recent line");
                vm.SelectedSession = new LogSession
                {
                    LogFilePath = file,
                    SessionDirectory = root,
                    Status = SessionStatus.Stopped
                };
                vm.SearchText = "older important";

                await vm.SearchEntireSessionCommand.ExecuteAsync(null);

                vm.LogEntriesView.Should().ContainSingle(e => e.RawLine == "older important line");
                vm.IsShowingFullFileSearch.Should().BeTrue();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Snapshot_UsesSelectedSessionsDeviceWhenAnotherDeviceIsSelected()
    {
        var a = new DeviceInfo { Serial = "A", Platform = DevicePlatform.Android };
        var b = new DeviceInfo { Serial = "B", Platform = DevicePlatform.Android };
        var root = Path.Combine(Path.GetTempPath(), $"LogProSnapshot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var monitor = new Mock<IDeviceMonitorService>();
            monitor.Setup(x => x.CurrentDevices).Returns(new List<DeviceInfo> { a, b });
            var sessions = new Mock<ISessionService>();
            sessions.Setup(x => x.ActiveSessions).Returns(new List<LogSession>());
            sessions.Setup(x => x.GetSavedSessions()).Returns(new List<LogSession>());
            var adb = new Mock<IAdbService>();
            adb.Setup(x => x.CaptureScreenshotAsync("A", It.IsAny<string>())).ReturnsAsync(true);
            using var vm = new SessionViewModel(sessions.Object, adb.Object,
                new Mock<IIosService>().Object, monitor.Object, new ImmediateUiDispatcher());
            vm.SelectedSession = new LogSession
            {
                DeviceSerial = "A",
                DeviceId = LogPro.Helpers.SecurityHelper.HashSerial("A"),
                SessionDirectory = root
            };
            vm.SelectedDevice = b;

            await vm.TakeSnapshotCommand.ExecuteAsync(null);

            adb.Verify(x => x.CaptureScreenshotAsync("A", It.IsAny<string>()), Times.Once);
            adb.Verify(x => x.CaptureScreenshotAsync("B", It.IsAny<string>()), Times.Never);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SnapshotWithoutSession_UsesConfiguredSessionsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProConfigured_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var device = new DeviceInfo { Serial = "A", Platform = DevicePlatform.Android };
            var monitor = new Mock<IDeviceMonitorService>();
            monitor.Setup(x => x.CurrentDevices).Returns(new List<DeviceInfo> { device });
            var sessions = new Mock<ISessionService>();
            sessions.Setup(x => x.ActiveSessions).Returns(new List<LogSession>());
            sessions.Setup(x => x.GetSavedSessions()).Returns(new List<LogSession>());
            sessions.Setup(x => x.SessionsRootDirectory).Returns(root);
            var adb = new Mock<IAdbService>();
            string? capturedPath = null;
            adb.Setup(x => x.CaptureScreenshotAsync("A", It.IsAny<string>()))
                .Callback<string, string>((_, path) => capturedPath = path).ReturnsAsync(true);
            using var vm = new SessionViewModel(sessions.Object, adb.Object,
                new Mock<IIosService>().Object, monitor.Object, new ImmediateUiDispatcher());

            await vm.TakeSnapshotCommand.ExecuteAsync(null);

            capturedPath.Should().StartWith(root);
        }
        finally { Directory.Delete(root, true); }
    }
}
