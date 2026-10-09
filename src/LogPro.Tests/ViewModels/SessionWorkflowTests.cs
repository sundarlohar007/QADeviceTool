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
    public async Task ManualStop_RemainsStoppedOnReadinessEvent()
    {
        var device = new DeviceInfo { Serial = "A", Platform = DevicePlatform.Android };
        var (vm, sessions) = Create(device);
        using (vm)
        {
            vm.AutoCapture = true;
            var session = new LogSession { DeviceSerial = "A", Status = SessionStatus.Capturing };
            vm.SelectedSession = session;
            sessions.Raise(x => x.CaptureStarted += null, session);
            vm.StopCaptureCommand.Execute(null);
            var monitor = (IDeviceMonitorService)typeof(SessionViewModel).GetField("_deviceMonitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
            Mock.Get(monitor).Raise(x => x.DeviceConnected += null, device);
            await Task.Delay(50);
            sessions.Verify(x => x.CreateSession(It.IsAny<DeviceInfo>(), It.IsAny<string>()), Times.Never);
        }
    }

    [Fact]
    public async Task Filtering_LatestSnapshotWins_AndRawModeDoesNotReparseHistory()
    {
        var (vm, _) = Create();
        using (vm)
        {
            vm.LogEntries.AddRange(Enumerable.Range(0, 20000).Select(i => new LogEntry
            { RawLine = "entry " + i, Message = "entry " + i, Level = i % 2 == 0 ? LogLevel.Error : LogLevel.Info }));
            var original = vm.LogEntries[0];
            foreach (var level in vm.LogLevelFilters) level.IsSelected = false;
            vm.LogLevelFilters.Single(f => f.Level == LogLevel.Error).IsSelected = true;
            await vm.FilteringTask;
            vm.LogEntriesView.Should().HaveCount(10000).And.OnlyContain(e => e.Level == LogLevel.Error);
            vm.IsRawMode = !vm.IsRawMode;
            vm.LogEntries[0].Should().BeSameAs(original);
            foreach (var level in vm.LogLevelFilters) level.IsSelected = false;
            await vm.FilteringTask;
            vm.LogEntriesView.Should().BeEmpty();
        }
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
    public async Task PauseDisplay_KeepsCapturedLinesAndReplaysOnResume()
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
            await vm.FilteringTask;
            vm.LogEntriesView.Should().HaveCount(1);
            vm.IsRawMode = false;
            vm.LogEntriesView[0].Message.Should().Be("failed");
            vm.LogEntriesView[0].Tag.Should().Be("Example");
        }
    }

    [Fact]
    public void LiveLongRecords_ResetSeverityBetweenRecords()
    {
        var (vm, sessions) = Create();
        using (vm)
        {
            var session = new LogSession { DeviceSerial = "A", Status = SessionStatus.Capturing, Format = LogcatFormat.Long };
            vm.SelectedSession = session;
            sessions.Raise(x => x.CaptureStarted += null, session);
            sessions.Raise(x => x.LogBatchReceived += null, session.Id,
                "[ 10-06 12:00:00.000 123: 456 E/Tag ]\r\ncontinued\r\n\r\nunattributed\r\n");
            vm.LogEntries.Single(e => e.RawLine == "continued").Level.Should().Be(LogLevel.Error);
            vm.LogEntries.Single(e => e.RawLine == "unattributed").Level.Should().Be(LogLevel.Unknown);
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

    [Theory]
    [InlineData(LogcatFormat.ThreadTime)]
    [InlineData(LogcatFormat.Long)]
    public async Task SearchEntireSession_FindsLinesOutsideViewerTail(LogcatFormat format)
    {
        var root = Path.Combine(Path.GetTempPath(), $"LogProSearch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "capture_log.txt");
            var header = format == LogcatFormat.Long ? "[ 10-06 12:00:00.000 123: 456 E/Tag ]\n" : "";
            await File.WriteAllTextAsync(file, header + "older important line\n\nrecent line\n");
            var (vm, sessions) = Create();
            using (vm)
            {
                sessions.Setup(x => x.ReadLogContentAsync(It.IsAny<LogSession>(), It.IsAny<int>()))
                    .ReturnsAsync("recent line");
                vm.SelectedSession = new LogSession
                {
                    LogFilePath = file,
                    SessionDirectory = root,
                    Format = format,
                    Status = SessionStatus.Stopped
                };
                vm.SearchText = "older important";

                await vm.SearchEntireSessionCommand.ExecuteAsync(null);
                await vm.FilteringTask;

                vm.LogEntriesView.Should().ContainSingle(e => e.RawLine == "older important line");
                if (format == LogcatFormat.Long)
                {
                    vm.LogEntriesView[0].Level.Should().Be(LogLevel.Error);
                    vm.LogEntriesView[0].Tag.Should().Be("Tag");
                }
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
