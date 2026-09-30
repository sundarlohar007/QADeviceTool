using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace LogPro.Tests.ViewModels;

public class DashboardIntegrationTests
{
    private static DeviceInfo Android(string serial, DeviceConnectionState state = DeviceConnectionState.Online) =>
        new() { Serial = serial, Name = serial, Platform = DevicePlatform.Android, ConnectionState = state };

    private static (DashboardViewModel Dashboard, SessionViewModel Sessions,
        Mock<IDeviceMonitorService> Monitor, Mock<ISessionService> Capture,
        Mock<IAdbService> Adb, Mock<IScrcpyService> Scrcpy, DeviceStore Store, List<LogSession> Active) Create(List<DeviceInfo> devices, string? outputRoot = null)
    {
        var dispatcher = new ImmediateUiDispatcher();
        var store = new DeviceStore(dispatcher);
        store.UpdateDevices(devices);
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(x => x.CurrentDevices).Returns(() => devices);
        var active = new List<LogSession>();
        var capture = new Mock<ISessionService>();
        capture.Setup(x => x.ActiveSessions).Returns(() => active.ToList());
        capture.Setup(x => x.GetSavedSessions()).Returns(new List<LogSession>());
        capture.SetupProperty(x => x.SessionsRootDirectory, outputRoot ?? Path.GetTempPath());
        var adb = new Mock<IAdbService>();
        var ios = new Mock<IIosService>();
        var scrcpy = new Mock<IScrcpyService>();
        adb.Setup(x => x.CheckAvailabilityAsync()).ReturnsAsync(new ToolStatus { Name = "ADB", IsInstalled = true });
        ios.Setup(x => x.CheckAvailabilityAsync()).ReturnsAsync(new ToolStatus { Name = "iOS", IsInstalled = true });
        scrcpy.Setup(x => x.CheckAvailabilityAsync()).ReturnsAsync(new ToolStatus { Name = "scrcpy", IsInstalled = true });
        var checker = new DependencyChecker(adb.Object, ios.Object, scrcpy.Object);
        var sessions = new SessionViewModel(capture.Object, adb.Object, ios.Object, monitor.Object, dispatcher);
        var dashboard = new DashboardViewModel(adb.Object, ios.Object, scrcpy.Object, capture.Object,
            monitor.Object, checker, store, dispatcher);
        return (dashboard, sessions, monitor, capture, adb, scrcpy, store, active);
    }

    [Fact]
    public void DeviceRefresh_PreservesExplicitSelectionAndDisablesActionsWhenUnauthorized()
    {
        var devices = new List<DeviceInfo> { Android("A"), Android("B") };
        var context = Create(devices);
        using var dashboard = context.Dashboard;
        using var sessions = context.Sessions;

        dashboard.SelectedDevice = dashboard.Devices[1];
        context.Store.SelectedDevice!.Serial.Should().Be("B");

        devices = new List<DeviceInfo> { Android("A"), Android("B", DeviceConnectionState.Unauthorized) };
        context.Monitor.Raise(x => x.DevicesChanged += null, devices);

        dashboard.SelectedDevice!.Serial.Should().Be("B");
        dashboard.SelectedDevice.ConnectionState.Should().Be(DeviceConnectionState.Unauthorized);
        dashboard.CanUseSelectedDevice.Should().BeFalse();
        dashboard.OnlineDeviceCount.Should().Be(1);
        dashboard.DevicesNeedingAttentionCount.Should().Be(1);
    }

    [Fact]
    public void ExternalCapture_IsVisibleAndStoppableInSessions_AndCountTracksLifecycle()
    {
        var context = Create(new List<DeviceInfo> { Android("A") });
        using var dashboard = context.Dashboard;
        using var sessions = context.Sessions;
        var session = new LogSession { DeviceSerial = "A", DeviceName = "A", Status = SessionStatus.Capturing };
        context.Active.Add(session);

        context.Capture.Raise(x => x.CaptureStarted += null, session);

        dashboard.ActiveSessionCount.Should().Be(1);
        sessions.Sessions.Should().Contain(session);
        sessions.SelectedSession.Should().BeSameAs(session);
        sessions.IsCapturing.Should().BeTrue();

        context.Capture.Setup(x => x.GetActiveSessionForDevice("A")).Returns(session);
        context.Monitor.Raise(x => x.DeviceDisconnected += null, Android("A"));
        context.Capture.Verify(x => x.StopCapture(session), Times.Once);

        context.Active.Clear();
        session.Status = SessionStatus.Stopped;
        context.Capture.Raise(x => x.CaptureStopped += null, session);
        dashboard.ActiveSessionCount.Should().Be(0);
        sessions.IsCapturing.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_UsesDistinctPathsForRapidCaptures()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"LogProDashboardTest_{Guid.NewGuid():N}");
        var context = Create(new List<DeviceInfo> { Android("A") }, outputRoot);
        using var dashboard = context.Dashboard;
        using var sessions = context.Sessions;
        var paths = new List<string>();
        context.Adb.Setup(x => x.CaptureScreenshotAsync("A", It.IsAny<string>()))
            .Callback<string, string>((_, path) => paths.Add(path)).ReturnsAsync(true);
        try
        {
            await dashboard.QuickSnapshotCommand.ExecuteAsync(null);
            await dashboard.QuickSnapshotCommand.ExecuteAsync(null);

            paths.Should().HaveCount(2);
            paths[0].Should().NotBe(paths[1]);
            paths.Should().OnlyContain(p => p.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task QuickStart_DoesNotCreateDuplicateCaptureForDevice()
    {
        var context = Create(new List<DeviceInfo> { Android("A") });
        using var dashboard = context.Dashboard;
        using var sessions = context.Sessions;
        context.Capture.Setup(x => x.GetActiveSessionForDevice("A"))
            .Returns(new LogSession { DeviceSerial = "A", Status = SessionStatus.Capturing });

        await dashboard.QuickStartSessionCommand.ExecuteAsync(null);

        context.Capture.Verify(x => x.CreateSession(It.IsAny<DeviceInfo>(), It.IsAny<string?>()), Times.Never);
        dashboard.WelcomeMessage.Should().Contain("already running");
    }

    [Fact]
    public async Task FailedQuickStart_RemovesEmptySessionDirectory()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"LogProDashboardTest_{Guid.NewGuid():N}");
        var sessionDir = Path.Combine(outputRoot, "failed-capture");
        Directory.CreateDirectory(sessionDir);
        var context = Create(new List<DeviceInfo> { Android("A") }, outputRoot);
        using var dashboard = context.Dashboard;
        using var sessions = context.Sessions;
        context.Capture.Setup(x => x.CreateSession(It.IsAny<DeviceInfo>(), It.IsAny<string?>()))
            .Returns(new LogSession { DeviceSerial = "A", SessionDirectory = sessionDir });
        context.Capture.Setup(x => x.StartCaptureAsync(It.IsAny<LogSession>(), It.IsAny<LogcatBuffer>(), It.IsAny<LogcatFormat>()))
            .ReturnsAsync(false);
        try
        {
            await dashboard.QuickStartSessionCommand.ExecuteAsync(null);
            Directory.Exists(sessionDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MirrorButton_StopsExistingMirrorForSelectedDevice()
    {
        var context = Create(new List<DeviceInfo> { Android("A") });
        using var dashboard = context.Dashboard;
        using var sessions = context.Sessions;
        context.Scrcpy.Setup(x => x.IsRunning).Returns(true);
        context.Scrcpy.Setup(x => x.MirroredDeviceSerial).Returns("A");

        await dashboard.QuickMirrorCommand.ExecuteAsync(null);

        context.Scrcpy.Verify(x => x.StopMirroring(), Times.Once);
        context.Scrcpy.Verify(x => x.StartMirroringAsync(It.IsAny<string>(), It.IsAny<ScrcpyOptions?>()), Times.Never);
    }

    [Fact]
    public void DiscoveryFailureAndIosLimitation_AreVisibleInDashboardState()
    {
        var device = new DeviceInfo
        {
            Serial = "ios-1",
            Name = "iPhone",
            Platform = DevicePlatform.iOS,
            ConnectionState = DeviceConnectionState.Online
        };
        var context = Create(new List<DeviceInfo> { device });
        using var dashboard = context.Dashboard;
        using var sessions = context.Sessions;

        context.Monitor.Raise(x => x.DiscoveryStatusChanged += null, "Android discovery failed.");

        dashboard.HasPlatformNotice.Should().BeTrue();
        dashboard.CanMirrorSelectedDevice.Should().BeFalse();
        dashboard.HasDiscoveryError.Should().BeTrue();
        dashboard.DiscoveryMessage.Should().Contain("Android discovery failed");
    }

    [Fact]
    public void SelectingDeviceInEitherTab_UpdatesTheOtherTab()
    {
        var devices = new List<DeviceInfo> { Android("A"), Android("B") };
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(x => x.CurrentDevices).Returns(devices);
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.GetDeviceDetailsAsync(It.IsAny<DeviceInfo>())).ReturnsAsync((DeviceInfo device) => device);
        adb.Setup(x => x.CheckAvailabilityAsync()).ReturnsAsync(new ToolStatus { IsInstalled = true });
        var ios = new Mock<IIosService>();
        ios.Setup(x => x.CheckAvailabilityAsync()).ReturnsAsync(new ToolStatus { IsInstalled = true });
        var scrcpy = new Mock<IScrcpyService>();
        scrcpy.Setup(x => x.CheckAvailabilityAsync()).ReturnsAsync(new ToolStatus { IsInstalled = true });
        var capture = new Mock<ISessionService>();
        capture.Setup(x => x.ActiveSessions).Returns(new List<LogSession>());
        capture.Setup(x => x.GetSavedSessions()).Returns(new List<LogSession>());
        using var provider = new ServiceCollection()
            .AddSingleton<IUiDispatcher, ImmediateUiDispatcher>()
            .AddSingleton<IDeviceStore, DeviceStore>()
            .AddSingleton(adb.Object)
            .AddSingleton(ios.Object)
            .AddSingleton(scrcpy.Object)
            .AddSingleton(capture.Object)
            .AddSingleton(monitor.Object)
            .AddSingleton<DependencyChecker>()
            .AddSingleton<MainViewModel>()
            .BuildServiceProvider();
        using var main = provider.GetRequiredService<MainViewModel>();

        main.DeviceVM.SelectedDevice = devices[1];
        main.DashboardVM.SelectedDevice!.Serial.Should().Be("B");

        main.DashboardVM.SelectedDevice = main.DashboardVM.Devices[0];
        main.DeviceVM.SelectedDevice!.Serial.Should().Be("A");

        monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo>());
        main.DashboardVM.SelectedDevice.Should().BeNull();
        main.DeviceVM.SelectedDevice.Should().BeNull();
    }
}
