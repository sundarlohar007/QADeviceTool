using LogPro.Models;
using LogPro.Services;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests.ViewModels;

public class StressTestWorkflowTests
{
    private static StressTestViewModel CreateVm(IMonkeyProcessRunner runner)
    {
        var device = new DeviceInfo
        {
            Serial = "FAKE01",
            Name = "Test Pixel",
            Platform = DevicePlatform.Android,
            ConnectionState = DeviceConnectionState.Online
        };
        var monitor = new Mock<IDeviceMonitorService>();
        monitor.SetupGet(x => x.CurrentDevices).Returns(new[] { device });
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.ExecuteCommandWithResultAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string command, CancellationToken _) => command.StartsWith("shell pm path")
                ? (true, "package:/data/app/base.apk", "") : (true, "", ""));
        adb.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
        return new StressTestViewModel(adb.Object, monitor.Object, new ImmediateUiDispatcher(), runner)
        {
            TargetPackage = "com.example.app"
        };
    }

    [Fact]
    public async Task Run_ParsesAospProgressAndResetsCountsBetweenRuns()
    {
        var runner = new Mock<IMonkeyProcessRunner>();
        var run = 0;
        runner.Setup(x => x.RunAsync(It.IsAny<MonkeyRunOptions>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MonkeyRunOptions options, Action<string> onLine, CancellationToken _) =>
            {
                run++;
                onLine("// Sending event #100");
                if (run == 1) onLine("// CRASH: com.example.app");
                onLine($"Events injected: {options.EventCount}");
                onLine("// Monkey finished");
                return new MonkeyExecutionResult(0, false, true);
            });
        using var vm = CreateVm(runner.Object);
        vm.CanRun.Should().BeTrue();

        await vm.RunMonkeyCommand.ExecuteAsync(null);
        vm.RunHistory.Should().HaveCount(1);
        vm.RunHistory[0].Summary.Result.Should().Be("FAILED");
        vm.CrashCount.Should().Be(1);
        vm.Output.Should().Contain("// Sending event #100");

        await vm.RunMonkeyCommand.ExecuteAsync(null);
        vm.RunHistory.Should().HaveCount(2);
        vm.RunHistory[0].Summary.Result.Should().Be("PASSED");
        vm.CrashCount.Should().Be(0);
        vm.EventsInjected.Should().Be(vm.EventCount);
    }

    [Fact]
    public async Task Run_WithoutFinishMarker_IsError()
    {
        var runner = new Mock<IMonkeyProcessRunner>();
        runner.Setup(x => x.RunAsync(It.IsAny<MonkeyRunOptions>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonkeyExecutionResult(0, false, true));
        using var vm = CreateVm(runner.Object);
        await vm.RunMonkeyCommand.ExecuteAsync(null);
        vm.RunHistory.Single().Summary.Result.Should().Be("ERROR");
    }

    [Fact]
    public async Task Run_CancelledByRunner_IsNotReportedAsPass()
    {
        var runner = new Mock<IMonkeyProcessRunner>();
        runner.Setup(x => x.RunAsync(It.IsAny<MonkeyRunOptions>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MonkeyRunOptions _, Action<string> onLine, CancellationToken _) =>
            {
                onLine("// Sending event #100");
                return new MonkeyExecutionResult(-1, true, true);
            });
        using var vm = CreateVm(runner.Object);
        await vm.RunMonkeyCommand.ExecuteAsync(null);
        vm.RunHistory.Single().Summary.Result.Should().Be("CANCELLED");
        vm.EventsInjected.Should().Be(100);
    }
}
