using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class FileExplorerWorkflowTests
{
    [Fact]
    public async Task ReconnectingDevice_DisablesTransferAndDirectoryQueries()
    {
        var context = Create(new List<DeviceInfo> { Android("A") });
        using var vm = context.Vm;
        var unavailable = Android("A");
        unavailable.IsTemporarilyUnavailable = true;
        context.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { unavailable });
        vm.CanTransfer.Should().BeFalse();
        context.Adb.Invocations.Clear();
        await vm.LoadDirectoryCommand.ExecuteAsync("/sdcard/DCIM");
        context.Adb.Verify(x => x.ListDirectoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static DeviceInfo Android(string serial) => new()
    {
        Serial = serial,
        Name = serial,
        Platform = DevicePlatform.Android,
        ConnectionState = DeviceConnectionState.Online
    };

    private static (FileExplorerViewModel Vm, Mock<IDeviceMonitorService> Monitor,
        Mock<IAdbService> Adb) Create(List<DeviceInfo> devices)
    {
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(x => x.CurrentDevices).Returns(() => devices);
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.ListDirectoryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string path) => new List<DeviceFile>
            { new() { Name = "item.txt", Path = path.TrimEnd('/') + "/item.txt" } });
        return (new FileExplorerViewModel(adb.Object, new Mock<IIosService>().Object,
            monitor.Object, new ImmediateUiDispatcher()), monitor, adb);
    }

    [Fact]
    public async Task MetadataRefresh_KeepsCurrentDirectoryAndSelection()
    {
        var devices = new List<DeviceInfo> { Android("A") };
        var context = Create(devices);
        using var vm = context.Vm;
        await vm.LoadDirectoryCommand.ExecuteAsync("/sdcard/DCIM");
        var replacement = Android("A");
        replacement.BatteryLevel = "50%";
        context.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { replacement });

        vm.CurrentPath.Should().Be("/sdcard/DCIM");
        vm.SelectedDevice!.Serial.Should().Be(replacement.Serial);
        vm.Files.Should().Contain(f => f.Name == "item.txt");
    }

    [Fact]
    public async Task AddingAnotherDevice_DoesNotResetCurrentFolder()
    {
        var context = Create(new List<DeviceInfo> { Android("A") });
        using var vm = context.Vm;
        await vm.LoadDirectoryCommand.ExecuteAsync("/sdcard/DCIM");
        context.Monitor.Raise(x => x.DevicesChanged += null,
            new List<DeviceInfo> { Android("A"), Android("B") });

        vm.CurrentPath.Should().Be("/sdcard/DCIM");
        vm.SelectedDevice!.Serial.Should().Be("A");
        vm.AvailableDevices.Should().HaveCount(2);
    }

    [Fact]
    public async Task InvalidTypedPath_DoesNotBecomeTransferDestination()
    {
        var context = Create(new List<DeviceInfo> { Android("A") });
        using var vm = context.Vm;
        await vm.LoadDirectoryCommand.ExecuteAsync("/sdcard/DCIM");
        vm.PathInput = "/sdcard/../data";
        await vm.NavigateToPathCommand.ExecuteAsync(null);

        vm.CurrentPath.Should().Be("/sdcard/DCIM");
        vm.StatusMessage.Should().Contain("absolute device path");
    }

    [Fact]
    public async Task LateDirectoryResult_AfterDisconnect_DoesNotRepopulateFiles()
    {
        var devices = new List<DeviceInfo> { Android("A") };
        var context = Create(devices);
        using var vm = context.Vm;
        var pending = new TaskCompletionSource<List<DeviceFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Adb.Setup(x => x.ListDirectoryAsync("A", "/sdcard/slow"))
            .Returns(pending.Task);
        var load = vm.LoadDirectoryCommand.ExecuteAsync("/sdcard/slow");
        context.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo>());
        pending.SetResult(new List<DeviceFile> { new() { Name = "stale", Path = "/sdcard/slow/stale" } });
        await load;

        vm.SelectedDevice.Should().BeNull();
        vm.Files.Should().BeEmpty();
        vm.StatusMessage.Should().Be("Device disconnected.");
    }
}
