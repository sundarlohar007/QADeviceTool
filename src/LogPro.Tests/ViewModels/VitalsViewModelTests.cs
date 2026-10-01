using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class VitalsViewModelTests
{
    private static DeviceInfo Device(string serial, DevicePlatform platform = DevicePlatform.Android) =>
        new()
        {
            Serial = serial,
            Platform = platform,
            ConnectionState = DeviceConnectionState.Online,
            BatteryLevel = "87",
            BatteryStatus = "Charging"
        };

    [Fact]
    public void Parser_NormalizesMulticoreCpuAndBatteryStatus()
    {
        var sample = VitalsParsers.Parse(DateTimeOffset.UtcNow, "A",
            "Total RAM: 4,194,304K\nFree RAM: 1,048,576K",
            "400%cpu 22%user 8%sys 370%idle",
            "level: 87\nstatus: 5\nhealth: 2\ntemperature: 310",
            "Thermal status: 2", "SSID: \"Lab WiFi\"", "default via 192.0.2.1 dev wlan0 src 192.0.2.10",
            "", null, null, "", 120);
        sample.CpuPercent.Should().Be(7.5);
        sample.MemoryPercent.Should().Be(75);
        sample.BatteryState.Should().Be("Full");
        sample.BatteryTemperatureCelsius.Should().Be(31);
        sample.ThermalSeverity.Should().Be("Moderate");
    }

    [Fact]
    public void Parser_InvalidReadingsRemainUnavailable()
    {
        var sample = VitalsParsers.Parse(DateTimeOffset.UtcNow, "A", "Total RAM: 100K\nFree RAM: 200K",
            "not top", "no battery", "unknown", "", "", "", null, null, "", -1);
        sample.CpuPercent.Should().BeNull();
        sample.MemoryPercent.Should().BeNull();
        sample.BatteryPercent.Should().BeNull();
        sample.BatteryTemperatureCelsius.Should().BeNull();
        sample.SampleDurationMs.Should().BeNull();
    }

    [Fact]
    public void DiskCounters_UsesWholeDisksWithoutDoubleCountingPartitions()
    {
        var counters = VitalsParsers.ParseDiskCounters(
            " 179 0 mmcblk0 1 0 100 0 1 0 200 0 0 0 0\n" +
            " 179 1 mmcblk0p1 1 0 90 0 1 0 190 0 0 0 0\n" +
            " 8 0 sda 1 0 30 0 1 0 40 0 0 0 0");
        counters.Should().NotBeNull();
        counters!.Value.ReadSectors.Should().Be(130);
        counters.Value.WriteSectors.Should().Be(240);
    }

    [Fact]
    public void DeviceSelection_ClearsOldReadingsAndShowsIosCapability()
    {
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.SetupGet(x => x.CurrentDevices).Returns([Device("A"), Device("I", DevicePlatform.iOS)]);
        using var vm = new VitalsViewModel(Mock.Of<IAdbService>(), monitor.Object, new ImmediateUiDispatcher());
        vm.SelectedDevice!.Serial.Should().Be("A");
        vm.CpuPercent = 88;
        vm.RecordedSamples.Add(new VitalsSample { DeviceSerial = "A" });
        vm.OnDeviceSelected(Device("I", DevicePlatform.iOS));
        vm.SelectedDevice!.Serial.Should().Be("I");
        vm.CpuDisplay.Should().Be("--");
        vm.BatteryDisplay.Should().Be("87%");
        vm.IsPollingSupported.Should().BeFalse();
        vm.CapabilityMessage.Should().Contain("iOS");
        vm.RecordedSamples.Should().ContainSingle("a device switch must not discard data awaiting export");
    }

    [Fact]
    public async Task DeviceSwitch_DiscardsInFlightSampleFromPreviousDevice()
    {
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.SetupGet(x => x.CurrentDevices).Returns([Device("A"), Device("B")]);
        var firstCommand = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        adb.Setup(x => x.ExecuteCommandAsync("A", "shell cat /proc/meminfo", It.IsAny<CancellationToken>()))
            .Returns(firstCommand.Task);
        using var vm = new VitalsViewModel(adb.Object, monitor.Object, new ImmediateUiDispatcher());
        vm.OnNavigatedTo();
        vm.SelectedDevice = Device("B");
        firstCommand.SetResult("MemTotal: 1000 kB\nMemAvailable: 100 kB");
        await Task.Delay(100);
        vm.History.Should().NotContain(x => x.DeviceSerial == "A");
        vm.SelectedDevice!.Serial.Should().Be("B");
    }

    [Fact]
    public void ManualPause_IsPreservedAcrossNavigation()
    {
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.SetupGet(x => x.CurrentDevices).Returns([Device("A")]);
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        using var vm = new VitalsViewModel(adb.Object, monitor.Object, new ImmediateUiDispatcher());
        vm.OnNavigatedTo();
        vm.IsPolling.Should().BeTrue();
        vm.TogglePollingCommand.Execute(null);
        vm.OnNavigatedFrom();
        vm.OnNavigatedTo();
        vm.IsPolling.Should().BeFalse();
    }

    [Fact]
    public void TemporaryUnavailability_PausesWithoutSwitchingDevices()
    {
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.SetupGet(x => x.CurrentDevices).Returns([Device("A"), Device("B")]);
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        using var vm = new VitalsViewModel(adb.Object, monitor.Object, new ImmediateUiDispatcher());
        vm.OnNavigatedTo();
        var missed = Device("A");
        missed.IsTemporarilyUnavailable = true;
        monitor.Raise(x => x.DevicesChanged += null, new List<DeviceInfo> { missed, Device("B") });
        vm.SelectedDevice!.Serial.Should().Be("A");
        vm.IsPolling.Should().BeFalse();
        vm.CapabilityMessage.Should().Contain("reconnecting");
    }

    [Fact]
    public void Csv_ExcludesNetworkIdentifiersByDefaultAndQuotesFormulas()
    {
        var sample = new VitalsSample
        {
            TimestampUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DeviceSerial = "=unsafe",
            CpuPercent = 12.5,
            Ssid = "Secret",
            IpAddress = "192.0.2.10"
        };
        var privateCsv = VitalsViewModel.BuildCsv([sample], false);
        privateCsv.Should().NotContain("=unsafe");
        privateCsv.Should().NotContain("Secret");
        privateCsv.Should().NotContain("192.0.2.10");
        VitalsViewModel.BuildCsv([sample], true).Should().Contain("'=unsafe").And.Contain("Secret");
    }
}
