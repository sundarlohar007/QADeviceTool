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
        ios.Setup(x => x.GetConnectedDevicesAsync()).ReturnsAsync(new List<DeviceInfo>());
        var monitor = new DeviceMonitorService(adb.Object, ios.Object);
        var disconnects = 0;
        monitor.DeviceDisconnected += _ => disconnects++;

        for (var i = 0; i < 4; i++) await monitor.PollDevicesAsync();

        monitor.CurrentDevices.Should().ContainSingle(d => d.Serial == device.Serial);
        disconnects.Should().Be(0);
        monitor.Dispose();
    }
}
