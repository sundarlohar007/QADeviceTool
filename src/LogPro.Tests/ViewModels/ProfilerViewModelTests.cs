using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class ProfilerViewModelTests
{
    private static Mock<IAdbService> CreateFakeAdb()
    {
        var mock = new Mock<IAdbService>();
        mock.Setup(a => a.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string serial, string command) => command switch
            {
                var c when c.Contains("SurfaceFlinger --list") =>
                    "SurfaceView[com.fakegame/com.fakegame.MainActivity](BLAST)#0\n",
                var c when c.Contains("SurfaceFlinger --latency") =>
                    SurfaceFlingerOutput(),
                var c when c.Contains("cpuinfo") =>
                    "  38% 2345/com.fakegame: 25% user + 13% kernel / faults: 42 minor\n",
                var c when c.Contains("meminfo") =>
                    "     TOTAL PSS:    384000\n     TOTAL RSS:    462000\n",
                var c when c.Contains("thermalservice") =>
                    "Thermal status: 0\n",
                var c when c.Contains("battery") =>
                    "level: 87\n",
                _ => string.Empty
            });
        return mock;
    }

    private static string SurfaceFlingerOutput()
    {
        var sb = new System.Text.StringBuilder("16666666\n");
        long present = 10_000_000_000L;
        for (var i = 0; i < 60; i++)
        {
            present += 16_666_666L;
            if (i % 10 == 0) present += 20_000_000L;
            sb.AppendLine($"{i * 16_666_666:D14}\t{i * 16_666_666 + 2_000_000:D14}\t{present:D14}");
        }
        return sb.ToString();
    }

    [Fact]
    public async Task StartStop_CapturesSnapshots()
    {
        var adb = CreateFakeAdb();
        var store = new DeviceStore(new ImmediateUiDispatcher());
        store.UpdateDevices(new[]
        {
            new LogPro.Models.DeviceInfo { Serial = "FAKE01", Model = "Pixel", Platform = LogPro.Models.DevicePlatform.Android }
        });

        var vm = new ProfilerViewModel(adb.Object, store, new ImmediateUiDispatcher(), packageOverride: "com.fakegame");
        vm.StartProfilingCommand.Execute(null);

        // Wait for the CONDITION (2 samples) with a generous deadline — fixed sleeps flake under CI load.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.History.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        await vm.StopProfilingCommand.ExecuteAsync(null);

        vm.History.Count.Should().BeGreaterThanOrEqualTo(2, "sampler runs at ~1s intervals");
        vm.Fps.Should().HaveValue();
        vm.Fps!.Value.Should().BeGreaterThan(30, "fake SurfaceFlinger streams ~60fps with jank");
        vm.JankyFrames.Should().BeGreaterThan(0, "fake stream injects jank frames");
        vm.CpuPercent.Should().Be(38.0);
        vm.PssKb.Should().Be(384000);
    }

    [Fact]
    public void Start_NoDevice_ShowsMessage()
    {
        var vm = new ProfilerViewModel(CreateFakeAdb().Object, new DeviceStore(new ImmediateUiDispatcher()), new ImmediateUiDispatcher());
        vm.StartProfilingCommand.Execute(null);
        vm.IsProfiling.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Select a device");
    }

    [Fact]
    public void Start_IosDevice_Blocked()
    {
        var adb = CreateFakeAdb();
        var store = new DeviceStore(new ImmediateUiDispatcher());
        store.UpdateDevices(new[]
        {
            new LogPro.Models.DeviceInfo { Serial = "ios1", Model = "iPhone", Platform = LogPro.Models.DevicePlatform.iOS }
        });
        var vm = new ProfilerViewModel(adb.Object, store, new ImmediateUiDispatcher());
        vm.StartProfilingCommand.Execute(null);
        vm.IsProfiling.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Android");
    }
}
