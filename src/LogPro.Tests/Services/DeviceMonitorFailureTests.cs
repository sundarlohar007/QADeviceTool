using LogPro.Models;
using LogPro.Services;
using Moq;

namespace LogPro.Tests.Services;

public class DeviceMonitorFailureTests
{
    [Fact]
    public async Task ConcurrentRefresh_CoalescesWhileBothPlatformsArePending()
    {
        var pending = new TaskCompletionSource<(bool Success, List<DeviceInfo> Devices)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var iosPending = new TaskCompletionSource<(bool Success, List<DeviceInfo> Devices)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.GetConnectedDevicesWithStatusAsync()).Returns(pending.Task);
        var ios = new Mock<IIosService>();
        ios.Setup(x => x.GetConnectedDevicesWithStatusAsync()).Returns(iosPending.Task);
        using var monitor = new DeviceMonitorService(adb.Object, ios.Object);

        var first = monitor.PollDevicesAsync();
        var second = monitor.PollDevicesAsync();
        try
        {
            second.Should().BeSameAs(first);
            second.IsCompleted.Should().BeFalse();
        }
        finally
        {
            pending.TrySetResult((true, new List<DeviceInfo> { new() { Serial = "A", Platform = DevicePlatform.Android } }));
            iosPending.TrySetResult((true, new List<DeviceInfo>()));
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        }
        adb.Verify(x => x.GetConnectedDevicesWithStatusAsync(), Times.Once);
        ios.Verify(x => x.GetConnectedDevicesWithStatusAsync(), Times.Once);
        monitor.CurrentDevices.Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_RestartsCompletedAndroidPollWithoutDuplicatingPendingIosPoll()
    {
        var iosPending = new TaskCompletionSource<(bool, List<DeviceInfo>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var androidPending = new TaskCompletionSource<(bool, List<DeviceInfo>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAndroidStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.GetConnectedDevicesWithStatusAsync()).Returns(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return Task.FromResult((true, new List<DeviceInfo>()));
            secondAndroidStarted.TrySetResult();
            return androidPending.Task;
        });
        var ios = new Mock<IIosService>();
        ios.Setup(x => x.GetConnectedDevicesWithStatusAsync()).Returns(iosPending.Task);
        using var monitor = new DeviceMonitorService(adb.Object, ios.Object);
        var first = monitor.PollDevicesAsync();
        var latest = first;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!secondAndroidStarted.Task.IsCompleted)
            {
                await Task.Delay(10, deadline.Token);
                latest = monitor.PollDevicesAsync();
            }
            first.IsCompleted.Should().BeFalse();
            latest.IsCompleted.Should().BeFalse();
        }
        finally
        {
            androidPending.TrySetResult((true, new List<DeviceInfo>()));
            iosPending.TrySetResult((true, new List<DeviceInfo>()));
            await Task.WhenAll(first, latest).WaitAsync(TimeSpan.FromSeconds(10));
        }
        adb.Verify(x => x.GetConnectedDevicesWithStatusAsync(), Times.Exactly(2));
        ios.Verify(x => x.GetConnectedDevicesWithStatusAsync(), Times.Once);
    }

    [Fact]
    public async Task MissedPoll_MarksDeviceReconnectingUntilItReturns()
    {
        var device = new DeviceInfo
        {
            Serial = "A",
            Platform = DevicePlatform.Android,
            ConnectionState = DeviceConnectionState.Online
        };
        var adb = new Mock<IAdbService>();
        adb.SetupSequence(x => x.GetConnectedDevicesWithStatusAsync())
            .ReturnsAsync((true, new List<DeviceInfo> { device }))
            .ReturnsAsync((true, new List<DeviceInfo>()))
            .ReturnsAsync((true, new List<DeviceInfo> { device }));
        var ios = new Mock<IIosService>();
        ios.Setup(x => x.GetConnectedDevicesWithStatusAsync()).ReturnsAsync((true, new List<DeviceInfo>()));
        using var monitor = new DeviceMonitorService(adb.Object, ios.Object);
        var updates = 0;
        monitor.DevicesChanged += _ => updates++;

        await monitor.PollDevicesAsync();
        await monitor.PollDevicesAsync();
        monitor.CurrentDevices.Single().IsTemporarilyUnavailable.Should().BeTrue();
        monitor.CurrentDevices.Single().StatusText.Should().Contain("Reconnecting");
        await monitor.PollDevicesAsync();
        monitor.CurrentDevices.Single().IsTemporarilyUnavailable.Should().BeFalse();
        updates.Should().Be(3);
    }

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
