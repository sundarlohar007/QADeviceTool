using LogPro.Services;

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
}
