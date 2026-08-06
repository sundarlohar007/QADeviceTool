using LogPro.Helpers;

namespace LogPro.Tests.Services;

public class ToolLauncherGateTests
{
    [Fact]
    public async Task SameDevice_SecondCommandWaitsForFirst()
    {
        var first = await ToolLauncher.TestAcquireAsync("-s R12345678 cmd");
        var second = await ToolLauncher.TestAcquireAsync("-s R12345678 cmd", waitMs: 50);
        second.Should().BeNull("same-device commands must serialize");

        first.Dispose();
        var after = await ToolLauncher.TestAcquireAsync("-s R12345678 cmd", waitMs: 50);
        after.Should().NotBeNull("gate must release when first command completes");
        after.Dispose();
    }

    [Fact]
    public async Task DifferentDevices_DoNotBlockEachOther()
    {
        var first = await ToolLauncher.TestAcquireAsync("-s DEVICE_A cmd");
        var second = await ToolLauncher.TestAcquireAsync("-s DEVICE_B cmd", waitMs: 50);
        second.Should().NotBeNull("different devices must run in parallel");
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task NoDeviceKey_UsesGlobalCapOnly()
    {
        var first = await ToolLauncher.TestAcquireAsync("version");
        first.Should().NotBeNull();
        first.Dispose();
    }
}
