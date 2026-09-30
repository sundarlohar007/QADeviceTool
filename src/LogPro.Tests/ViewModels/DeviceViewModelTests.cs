using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class DeviceViewModelTests
{
    private static DeviceInfo Device(string serial, DevicePlatform platform = DevicePlatform.Android) =>
        new() { Serial = serial, Name = serial, Platform = platform, ConnectionState = DeviceConnectionState.Online };

    private static (DeviceViewModel Vm, Mock<IDeviceMonitorService> Monitor, Mock<IAdbService> Adb,
        Mock<IIosService> Ios, Mock<IScrcpyService> Mirror, Mock<IPreferencesStore> Preferences,
        Mock<ISessionService> Sessions) Create(List<DeviceInfo> devices)
    {
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(x => x.CurrentDevices).Returns(() => devices);
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.GetDeviceDetailsAsync(It.IsAny<DeviceInfo>())).ReturnsAsync((DeviceInfo d) => d);
        var ios = new Mock<IIosService>();
        ios.Setup(x => x.GetDeviceDetailsAsync(It.IsAny<DeviceInfo>())).ReturnsAsync((DeviceInfo d) => d);
        var mirror = new Mock<IScrcpyService>();
        var preferences = new Mock<IPreferencesStore>();
        preferences.Setup(x => x.GetDevicePreference(It.IsAny<string>())).Returns(() => new DevicePreference());
        preferences.Setup(x => x.TrySaveDevicePreference(It.IsAny<string>(), It.IsAny<DevicePreference>())).Returns(true);
        var sessions = new Mock<ISessionService>();
        sessions.SetupProperty(x => x.SessionsRootDirectory, Path.GetTempPath());
        var vm = new DeviceViewModel(adb.Object, ios.Object, mirror.Object, monitor.Object,
            sessions.Object, new ImmediateUiDispatcher(), preferences.Object);
        return (vm, monitor, adb, ios, mirror, preferences, sessions);
    }

    [Fact]
    public void DiscoveryUpdate_PreservesSelectionAndUnsavedDraft()
    {
        var context = Create(new List<DeviceInfo> { Device("A"), Device("B") });
        using var vm = context.Vm;
        vm.SelectedDevice = vm.Devices[1];
        vm.DeviceNotes = "draft";
        var replacement = Device("B");
        replacement.BatteryLevel = "42%";

        context.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { Device("A"), replacement });

        vm.SelectedDevice.Should().BeSameAs(replacement);
        vm.DeviceNotes.Should().Be("draft");
        vm.CanTakeSnapshot.Should().BeTrue();
    }

    [Fact]
    public async Task LateDetails_DoNotOverwriteNewSelection()
    {
        var context = Create(new List<DeviceInfo> { Device("A"), Device("B") });
        using var vm = context.Vm;
        var late = new TaskCompletionSource<DeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Adb.Setup(x => x.GetDeviceDetailsAsync(It.Is<DeviceInfo>(d => d.Serial == "A")))
            .Returns(late.Task);
        vm.SelectedDevice = vm.Devices[0];
        vm.SelectedDevice = vm.Devices[1];
        late.SetResult(Device("A"));
        await Task.Yield();

        vm.DeviceDetails.Should().Contain("B").And.NotContain("Model: A");
        vm.SelectedDevice = null;
        vm.DeviceDetails.Should().Be("Select a device to view details.");
        vm.DeviceNotes.Should().BeEmpty();
    }

    [Fact]
    public async Task Snapshot_UsesCapturedDeviceWhenSelectionChanges()
    {
        var context = Create(new List<DeviceInfo> { Device("A"), Device("B", DevicePlatform.iOS) });
        using var vm = context.Vm;
        var directory = Path.Combine(Path.GetTempPath(), $"LogProDeviceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            context.Sessions.Object.SessionsRootDirectory = directory;
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Adb.Setup(x => x.CaptureScreenshotAsync("A", It.IsAny<string>()))
                .Returns(async (string _, string path) =>
                {
                    await pending.Task;
                    await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
                    return true;
                });
            vm.SelectedDevice = vm.Devices[0];
            var capture = vm.TakeSnapshotCommand.ExecuteAsync(null);
            vm.SelectedDevice = vm.Devices[1];
            pending.SetResult(true);
            await capture;

            context.Adb.Verify(x => x.CaptureScreenshotAsync("A", It.IsAny<string>()), Times.Once);
            context.Ios.Verify(x => x.CaptureScreenshotWithStatusAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            vm.StatusMessage.Should().StartWith("Snapshot saved:");
            Directory.GetFiles(directory, "*.png").Should().ContainSingle();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MirrorState_FollowsSharedServiceAndSelectedDevice()
    {
        var context = Create(new List<DeviceInfo> { Device("A"), Device("B") });
        using var vm = context.Vm;
        context.Mirror.Setup(x => x.IsRunning).Returns(true);
        context.Mirror.Setup(x => x.MirroredDeviceSerial).Returns("A");
        vm.SelectedDevice = vm.Devices[0];
        context.Mirror.Raise(x => x.StateChanged += null);
        vm.IsMirroring.Should().BeTrue();
        vm.CanMirror.Should().BeFalse();
        vm.SelectedDevice = vm.Devices[1];
        vm.IsMirroring.Should().BeFalse();
        vm.StopMirrorCommand.Execute(null);
        context.Mirror.Verify(x => x.StopMirroring(), Times.Never);
    }

    [Fact]
    public void SaveNotes_ReportsPersistenceFailure()
    {
        var context = Create(new List<DeviceInfo> { Device("A") });
        using var vm = context.Vm;
        context.Preferences.Setup(x => x.TrySaveDevicePreference(It.IsAny<string>(), It.IsAny<DevicePreference>())).Returns(false);
        vm.SelectedDevice = vm.Devices[0];
        vm.DeviceNotes = "unsaved";
        vm.SaveDeviceNotesCommand.Execute(null);
        vm.StatusMessage.Should().Contain("Could not save");
        vm.SelectedDevice = null;
        vm.SelectedDevice = vm.Devices[0];
        vm.DeviceNotes.Should().Be("unsaved");
    }
}
