using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class ShellViewModelTests
{
    private static DeviceInfo Device(string serial, DevicePlatform platform = DevicePlatform.iOS) => new()
    {
        Serial = serial,
        Name = serial,
        Platform = platform,
        ConnectionState = DeviceConnectionState.Online
    };

    private static (ShellViewModel Vm, Mock<IDeviceMonitorService> Monitor, Mock<IIosService> Ios)
        Create(params DeviceInfo[] devices)
        => Create(new ImmediateUiDispatcher(), devices);

    private static (ShellViewModel Vm, Mock<IDeviceMonitorService> Monitor, Mock<IIosService> Ios)
        Create(IUiDispatcher dispatcher, params DeviceInfo[] devices)
    {
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.Setup(x => x.CurrentDevices).Returns(devices);
        var ios = new Mock<IIosService>();
        return (new ShellViewModel(monitor.Object, ios.Object, dispatcher), monitor, ios);
    }

    [Fact]
    public void ExistingDevicesAppearImmediately_AndMetadataRefreshKeepsDraft()
    {
        var context = Create(Device("A"));
        using var vm = context.Vm;
        vm.SelectedDevice!.Serial.Should().Be("A");
        vm.CommandInput = "apps list";
        var replacement = Device("A");
        replacement.BatteryLevel = "50%";
        context.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { replacement });
        vm.CommandInput.Should().Be("apps list");
        vm.SelectedDevice.Should().BeSameAs(vm.Devices.Single());
    }

    [Fact]
    public void ExecuteIsEnabledOnlyForOnlineDeviceAndNonemptyInput()
    {
        var context = Create(Device("A"));
        using var vm = context.Vm;
        vm.ExecuteCommandCommand.CanExecute(null).Should().BeFalse();
        vm.CommandInput = "apps list";
        vm.ExecuteCommandCommand.CanExecute(null).Should().BeTrue();
        var unavailable = Device("A");
        unavailable.IsTemporarilyUnavailable = true;
        context.Monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { unavailable });
        vm.ExecuteCommandCommand.CanExecute(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("apps uninstall com.example.app", false)]
    [InlineData("afc push source target", false)]
    [InlineData("crash pull reports", false)]
    [InlineData("usbmux forward 1000 2000", false)]
    [InlineData("apps listing", false)]
    [InlineData("processes", false)]
    [InlineData("apps list", true)]
    [InlineData("afc ls /", true)]
    [InlineData("processes ps", true)]
    [InlineData("syslog live", true)]
    [InlineData("afc ls /foo;rm", false)]
    public void IosPolicy_AllowsOnlySupportedInspectionCommands(string command, bool expected)
        => ShellViewModel.IsSupportedIosCommand(command).Should().Be(expected);

    [Fact]
    public async Task FailedIosCommandShowsStdoutAndStderr()
    {
        var context = Create(Device("A"));
        using var vm = context.Vm;
        context.Ios.Setup(x => x.ExecuteCommandAsync("A", "apps list", It.IsAny<int>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolLauncherResult { Success = false, Output = "partial", Error = "permission denied", ExitCode = 2 });
        vm.CommandInput = "apps list";
        await vm.ExecuteCommandCommand.ExecuteAsync(null);
        vm.ShellOutput.Should().Contain("partial").And.Contain("permission denied");
        vm.StatusMessage.Should().Be("Command failed");
    }

    [Fact]
    public async Task SwitchingDevicesStopsCommand_AndKeepsItsOutputWithOriginalDevice()
    {
        var first = Device("A");
        var second = Device("B");
        var context = Create(first, second);
        using var vm = context.Vm;
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<ToolLauncherResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Ios.Setup(x => x.ExecuteCommandAsync("A", "apps list", It.IsAny<int>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string? _, string _, int _, Action<string>? _, CancellationToken token) =>
            {
                started.TrySetResult(token);
                return result.Task;
            });
        vm.CommandInput = "apps list";
        var execution = vm.ExecuteCommandCommand.ExecuteAsync(null);
        var token = await started.Task;
        vm.OnDeviceSelected(second);
        token.IsCancellationRequested.Should().BeTrue();
        result.SetResult(new ToolLauncherResult { Success = true, Output = "from A" });
        await execution;
        vm.ShellOutput.Should().NotContain("from A");
        vm.OnDeviceSelected(first);
        vm.ShellOutput.Should().Contain("from A");
    }

    [Fact]
    public async Task UnsupportedIosCommandNeverReachesCli()
    {
        var context = Create(Device("A"));
        using var vm = context.Vm;
        vm.CommandInput = "apps uninstall com.example.app";
        await vm.ExecuteCommandCommand.ExecuteAsync(null);
        context.Ios.Verify(x => x.ExecuteCommandAsync(It.IsAny<string?>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.ShellOutput.Should().Contain("Unsupported on iOS");
    }

    [Fact]
    public async Task AndroidWriteCommandShowsBlockedMessage()
    {
        var context = Create(Device("A", DevicePlatform.Android));
        using var vm = context.Vm;
        vm.CommandInput = "shell rm -rf /sdcard";
        await vm.ExecuteCommandCommand.ExecuteAsync(null);
        vm.ShellOutput.Should().Contain("[Blocked]");
        vm.CommandInput.Should().Be("shell rm -rf /sdcard");
    }

    [Fact]
    public async Task LargeOutputIsBoundedAndReportsTruncation()
    {
        var context = Create(Device("A"));
        using var vm = context.Vm;
        context.Ios.Setup(x => x.ExecuteCommandAsync("A", "apps list", It.IsAny<int>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolLauncherResult { Success = true, Output = new string('x', 100_000) });
        vm.CommandInput = "apps list";
        await vm.ExecuteCommandCommand.ExecuteAsync(null);
        vm.ShellOutput.Length.Should().BeLessThan(50_000);
        vm.ShellOutput.Should().Contain("[Earlier output truncated]");
    }

    [Fact]
    public async Task LiveLogCanBePausedResumedAndStopped()
    {
        TaskCompletionSource? postCompleted = null;
        var dispatcher = new Mock<IUiDispatcher>();
        dispatcher.Setup(x => x.Post(It.IsAny<Action>())).Callback<Action>(action =>
        {
            action();
            postCompleted?.TrySetResult();
        });
        var context = Create(dispatcher.Object, Device("A"));
        using var vm = context.Vm;
        Action<string>? stream = null;
        var running = new TaskCompletionSource<ToolLauncherResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Ios.Setup(x => x.ExecuteCommandAsync("A", "syslog live", It.Is<int>(t => t > 30_000),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string? _, string _, int _, Action<string>? callback, CancellationToken token) =>
            {
                stream = callback;
                token.Register(() => running.TrySetResult(new ToolLauncherResult { Error = "Process cancelled." }));
                return running.Task;
            });
        vm.IsOutputPaused = true;
        vm.CommandInput = "syslog live";
        var execution = vm.ExecuteCommandCommand.ExecuteAsync(null);
        stream.Should().NotBeNull();
        try
        {
            postCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stream!("line from device");
            await postCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            vm.ShellOutput.Should().NotContain("line from device");
            vm.IsOutputPaused = false;
            vm.ShellOutput.Should().Contain("line from device");
        }
        finally
        {
            vm.StopCommandCommand.Execute(null);
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        vm.StatusMessage.Should().Be("Stopped");
    }
}
