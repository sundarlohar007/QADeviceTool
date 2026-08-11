using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;

namespace LogPro.Tests.ViewModels;

/// <summary>Executes everything inline — no WPF Application required.</summary>
internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}

public class DeviceStoreTests
{
    private static readonly IUiDispatcher Dispatcher = new ImmediateUiDispatcher();

    private static DeviceInfo Android(string serial) =>
        new() { Serial = serial, Model = "Pixel", Platform = DevicePlatform.Android, ConnectionState = DeviceConnectionState.Online };

    [Fact]
    public void UpdateDevices_FirstUpdate_AutoSelectsFirst()
    {
        var store = new DeviceStore(Dispatcher);
        store.UpdateDevices(new[] { Android("A"), Android("B") });
        store.SelectedDevice.Should().NotBeNull();
        store.SelectedDevice!.Serial.Should().Be("A");
    }

    [Fact]
    public void UpdateDevices_SelectionPreserved_WhileStillConnected()
    {
        var store = new DeviceStore(Dispatcher);
        store.UpdateDevices(new[] { Android("A"), Android("B") });
        store.SelectedDevice = store.Devices[1];
        store.UpdateDevices(new[] { Android("B"), Android("A") }); // reordered, both still present
        store.SelectedDevice!.Serial.Should().Be("B");
    }

    [Fact]
    public void UpdateDevices_SelectedDisconnects_FallsBackToFirst()
    {
        var store = new DeviceStore(Dispatcher);
        store.UpdateDevices(new[] { Android("A"), Android("B") });
        store.SelectedDevice = store.Devices[1];
        store.UpdateDevices(new[] { Android("A") });
        store.SelectedDevice!.Serial.Should().Be("A");
    }

    [Fact]
    public void UpdateDevices_AllDisconnected_SelectionClears()
    {
        var store = new DeviceStore(Dispatcher);
        store.UpdateDevices(new[] { Android("A") });
        store.UpdateDevices(Array.Empty<DeviceInfo>());
        store.SelectedDevice.Should().BeNull();
    }

    [Fact]
    public void SelectedDevice_Set_RaisesChanged()
    {
        var store = new DeviceStore(Dispatcher);
        store.UpdateDevices(new[] { Android("A"), Android("B") }); // auto-selects A
        var fired = 0;
        store.Changed += () => fired++;
        store.SelectedDevice = store.Devices[1]; // different device — fires
        store.SelectedDevice = store.Devices[1]; // same serial — no re-fire
        fired.Should().Be(1);
    }
}

public class StressTestViewModelValidationTests
{
    private static StressTestViewModel CreateVm()
    {
        var adb = new AdbService();
        var monitor = new DeviceMonitorService(adb, new IosService());
        return new StressTestViewModel(adb, monitor, new ImmediateUiDispatcher());
    }

    [Fact]
    public async Task RunMonkey_NoDevice_Blocked()
    {
        var vm = CreateVm();
        await vm.RunMonkeyCommand.ExecuteAsync(null);
        vm.IsRunning.Should().BeFalse();
        vm.StatusMessage.Should().Contain("No device selected");
    }

    [Fact]
    public async Task RunMonkey_IosDevice_Blocked()
    {
        var vm = CreateVm();
        vm.SelectedDevice = new DeviceInfo { Serial = "ios1", Platform = DevicePlatform.iOS };
        await vm.RunMonkeyCommand.ExecuteAsync(null);
        vm.IsRunning.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Android-only");
    }

    [Fact]
    public async Task RunMonkey_PercentagesNotSummingTo100_Blocked()
    {
        var vm = CreateVm();
        vm.SelectedDevice = new DeviceInfo { Serial = "a1", Platform = DevicePlatform.Android };
        vm.TargetPackage = "com.example.app";
        vm.PctTouch = 99; // break the sum
        await vm.RunMonkeyCommand.ExecuteAsync(null);
        vm.IsRunning.Should().BeFalse();
        vm.StatusMessage.Should().Contain("sum to 100");
    }

    [Fact]
    public void DefaultPercentages_SumTo100()
    {
        var vm = CreateVm();
        (vm.PctTouch + vm.PctMotion + vm.PctTrackball + vm.PctNav + vm.PctSyskeys + vm.PctAppswitch)
            .Should().Be(100, "defaults must pass the built-in sum validation");
    }

    [Fact]
    public async Task RunMonkey_InvalidPackageName_Blocked()
    {
        var vm = CreateVm();
        vm.SelectedDevice = new DeviceInfo { Serial = "a1", Platform = DevicePlatform.Android };
        vm.TargetPackage = "bad;package";
        await vm.RunMonkeyCommand.ExecuteAsync(null);
        vm.IsRunning.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Invalid package name");
    }
}
