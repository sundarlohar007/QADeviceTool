using LogPro.Services;
using Moq;

namespace LogPro.Tests.Services;

public class MacroServiceParserTests
{
    [Fact]
    public void ParseMacro_GetEventLinesWithDevicePath_ParsesEventsAndCapturesInputDevice()
    {
        var raw = """
            [  12345.100000] /dev/input/event3: 0003 0039 00000123
            [  12345.150000] /dev/input/event3: 0003 0035 00000200
            [  12345.200000] /dev/input/event3: 0000 0000 00000000
            """;

        var macro = MacroService.ParseMacro(raw, "Tap");

        macro.Events.Should().HaveCount(3);
        macro.InputDevice.Should().Be("/dev/input/event3");
        macro.Events[1].DelayMs.Should().Be(50);
        macro.Events[1].Code.Should().Be(0x0035);
        macro.Events[1].Value.Should().Be(0x200);
    }

    [Fact]
    public void ParseMacro_UsesOnlySelectedTouchDevice()
    {
        var raw = "[ 1.000000] /dev/input/event1: 0003 0035 00000010\n" +
            "[ 1.010000] /dev/input/event2: 0003 0035 00000020\n" +
            "[ 1.020000] /dev/input/event1: 0000 0000 00000000";
        var macro = MacroService.ParseMacro(raw, "touch", inputDevicePath: "/dev/input/event1");
        macro.Events.Should().HaveCount(2);
        macro.Events[1].DelayMs.Should().Be(20);
    }

    [Fact]
    public void ParseTouchDevice_RequiresMultitouchPositionAxis()
    {
        var listing = "add device 1: /dev/input/event1\n  KEY_A\nadd device 2: /dev/input/event4\n  ABS_MT_POSITION_X";
        MacroService.ParseTouchDevice(listing).Should().Be("/dev/input/event4");
        MacroService.ParseTouchDevice("add device 1: /dev/input/event1\n  KEY_A").Should().BeNull();
    }

    [Fact]
    public async Task ReplaySimpleMacro_StopsOnFirstFailedAdbCommand()
    {
        var adb = new Mock<IAdbService>();
        var calls = 0;
        adb.Setup(x => x.ExecuteCommandWithResultAsync("serial", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { calls++; return (false, "", "permission denied"); });
        var service = new MacroService(adb.Object);
        var steps = new List<SimpleMacroStep> { new() { Action = "tap", X = 1, Y = 2, DelayMs = 0 }, new() { Action = "tap", X = 3, Y = 4 } };
        await FluentActions.Invoking(() => service.ReplaySimpleMacroAsync("serial", steps)).Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task SaveMacro_DoesNotReplaceExistingFileByDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "macro-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "macro.json");
            await MacroService.SaveMacroAsync(new MacroFile { Name = "first" }, path);
            await FluentActions.Invoking(() => MacroService.SaveMacroAsync(new MacroFile { Name = "second" }, path)).Should().ThrowAsync<IOException>();
            (await MacroService.LoadMacroAsync(path))!.Name.Should().Be("first");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ReplayRaw_BatchesZeroDelayEventsAndStopsOnFailure()
    {
        var adb = new Mock<IAdbService>();
        var commands = new List<string>();
        adb.Setup(x => x.ExecuteCommandWithResultAsync("serial", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string command, CancellationToken _) =>
            {
                commands.Add(command);
                return (commands.Count == 1, "", "denied");
            });
        var macro = new MacroFile
        {
            InputDevice = "/dev/input/event3",
            Events = [new() { Type = 3, Code = 53, Value = 10 }, new() { Type = 0 }, new() { Type = 3, Code = 53, Value = 11, DelayMs = 1 }]
        };
        await FluentActions.Invoking(() => new MacroService(adb.Object).ReplayMacroAsync("serial", macro))
            .Should().ThrowAsync<InvalidOperationException>();
        commands.Should().HaveCount(2);
        commands[0].Should().Contain(" && ");
    }

    [Fact]
    public async Task ReplayScreenshot_CreatesProtectedCheckpoint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "macro-screenshot-" + Guid.NewGuid().ToString("N"));
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.CaptureScreenshotAsync("serial", It.IsAny<string>())).ReturnsAsync(true);
        try
        {
            await new MacroService(adb.Object).ReplaySimpleMacroAsync("serial",
                [new SimpleMacroStep { Action = "screenshot", DelayMs = 0 }], screenshotDirectory: dir);
            adb.Verify(x => x.CaptureScreenshotAsync("serial", It.Is<string>(p => p.StartsWith(dir) && p.EndsWith(".png"))), Times.Once);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RawReplayCapability_RequiresWritableValidatedInputNode()
    {
        var adb = new Mock<IAdbService>();
        adb.Setup(x => x.ExecuteCommandWithResultAsync("serial", "shell test -w /dev/input/event7", It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "", ""));
        var service = new MacroService(adb.Object);
        (await service.CanInjectRawEventsAsync("serial", "/dev/input/event7")).Should().BeTrue();
        (await service.CanInjectRawEventsAsync("serial", "/dev/input/event7; rm")).Should().BeFalse();
        adb.Verify(x => x.ExecuteCommandWithResultAsync("serial", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
