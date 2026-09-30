using LogPro.Models;
using LogPro.Services;
using Moq;

namespace LogPro.Tests.Services;

public class DeviceMonitorFailureTests
{
    [Fact]
    public async Task FailedAdbPoll_DoesNotDisconnectPreviouslySeenDevice()
    {
        var device = new DeviceInfo
        {
            Serial = "emulator-5554",
            Platform = DevicePlatform.Android,
            ConnectionState = DeviceConnectionState.Online
        };
        var adb = new Mock<IAdbService>();
        adb.SetupSequence(x => x.GetConnectedDevicesWithStatusAsync())
            .ReturnsAsync((true, new List<DeviceInfo> { device }))
            .ReturnsAsync((false, new List<DeviceInfo>()))
            .ReturnsAsync((false, new List<DeviceInfo>()))
            .ReturnsAsync((false, new List<DeviceInfo>()));
        var ios = new Mock<IIosService>();
        ios.Setup(x => x.GetConnectedDevicesWithStatusAsync()).ReturnsAsync((true, new List<DeviceInfo>()));
        var monitor = new DeviceMonitorService(adb.Object, ios.Object);
        var disconnects = 0;
        var statusChanges = new List<string?>();
        monitor.DeviceDisconnected += _ => disconnects++;
        monitor.DiscoveryStatusChanged += statusChanges.Add;

        for (var i = 0; i < 4; i++) await monitor.PollDevicesAsync();

        monitor.CurrentDevices.Should().ContainSingle(d => d.Serial == device.Serial);
        disconnects.Should().Be(0);
        monitor.LastDiscoveryError.Should().Contain("Android discovery failed");
        statusChanges.Should().ContainSingle();
        monitor.Dispose();
    }

    [Fact]
    public async Task DiscoveryStatus_ClearsAfterAdbRecovers()
    {
        var adb = new Mock<IAdbService>();
        adb.SetupSequence(x => x.GetConnectedDevicesWithStatusAsync())
            .ReturnsAsync((false, new List<DeviceInfo>()))
            .ReturnsAsync((true, new List<DeviceInfo>()));
        var ios = new Mock<IIosService>();
        ios.Setup(x => x.GetConnectedDevicesWithStatusAsync()).ReturnsAsync((true, new List<DeviceInfo>()));
        using var monitor = new DeviceMonitorService(adb.Object, ios.Object);
        var statuses = new List<string?>();
        monitor.DiscoveryStatusChanged += statuses.Add;

        await monitor.PollDevicesAsync();
        await monitor.PollDevicesAsync();

        monitor.LastDiscoveryError.Should().BeNull();
        statuses.Should().HaveCount(2);
        statuses[0].Should().Contain("Android discovery failed");
        statuses[1].Should().BeNull();
    }

    [Fact]
    public async Task FailedIosPoll_RetainsPreviousDeviceAndReportsFailure()
    {
        var iosDevice = new DeviceInfo
        {
            Serial = "ios-1",
            Platform = DevicePlatform.iOS,
            ConnectionState = DeviceConnectionState.Online
        };
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.GetConnectedDevicesWithStatusAsync()).ReturnsAsync((true, new List<DeviceInfo>()));
        var ios = new Mock<IIosService>();
        ios.SetupSequence(x => x.GetConnectedDevicesWithStatusAsync())
            .ReturnsAsync((true, new List<DeviceInfo> { iosDevice }))
            .ReturnsAsync((false, new List<DeviceInfo>()));
        using var monitor = new DeviceMonitorService(adb.Object, ios.Object);

        await monitor.PollDevicesAsync();
        await monitor.PollDevicesAsync();

        monitor.CurrentDevices.Should().ContainSingle(d => d.Serial == "ios-1");
        monitor.LastDiscoveryError.Should().Contain("iOS discovery failed");
    }
}
