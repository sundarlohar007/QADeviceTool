using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class MacroViewModelTests
{
    [Fact]
    public async Task SwitchingToIos_ClearsAndroidCapabilityAndWarns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "macro-vm-" + Guid.NewGuid().ToString("N"));
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.SetupGet(x => x.CurrentDevices).Returns(new List<DeviceInfo>());
        var adb = new Mock<IAdbService>();
        using var vm = new MacroViewModel(new MacroService(adb.Object), adb.Object, monitor.Object,
            new ImmediateUiDispatcher(), directory);
        try
        {
            var ios = new DeviceInfo { Serial = "ios", Platform = DevicePlatform.iOS, ConnectionState = DeviceConnectionState.Online };
            monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { ios });
            vm.OnDeviceSelected(ios);
            vm.SelectedDevice.Should().BeSameAs(ios);
            vm.CanRawRecord.Should().BeFalse();
            vm.CapabilityMessage.Should().Contain("iOS");
            await vm.ToggleRecordingCommand.ExecuteAsync(null);
            vm.IsRecording.Should().BeFalse();
            vm.StatusMessage.Should().Contain("iOS");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NewSequence_SavesUniqueMacroWithoutDevice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "macro-vm-" + Guid.NewGuid().ToString("N"));
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.SetupGet(x => x.CurrentDevices).Returns(new List<DeviceInfo>());
        var adb = new Mock<IAdbService>();
        using var vm = new MacroViewModel(new MacroService(adb.Object), adb.Object, monitor.Object,
            new ImmediateUiDispatcher(), directory);
        try
        {
            vm.NewSequenceCommand.Execute(null);
            vm.DraftName = "Tap test";
            vm.AddStepCommand.Execute("tap");
            vm.SelectedDraftStep!.X = 10;
            vm.SelectedDraftStep.Y = 20;
            vm.SelectedDraftStep.DelayMs = 0;
            await vm.SaveSequenceCommand.ExecuteAsync(null);
            Directory.GetFiles(directory, "*.json").Should().ContainSingle();
            var saved = await MacroService.LoadMacroAsync(Directory.GetFiles(directory, "*.json")[0]);
            saved!.SimpleSteps.Should().ContainSingle(s => s.Action == "tap" && s.X == 10 && s.Y == 20);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
