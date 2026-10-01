using LogPro.Helpers;

namespace LogPro.Tests.Services;

[Collection("HeavyE2E")]
public class ToolLauncherGateTests
{
    [Fact]
    public void QuoteArgument_EscapesTrailingSlashAndEmbeddedQuote()
    {
        ToolLauncher.QuoteArgument(@"C:\dir\").Should().Be("\"C:\\dir\\\\\"");
        ToolLauncher.QuoteArgument("a\"b").Should().Be("\"a\\\"b\"");
    }

    [Fact]
    public async Task SameDevice_SecondCommandWaitsForFirst()
    {
        var first = await ToolLauncher.TestAcquireAsync("-s R12345678 cmd");
        first.Should().NotBeNull();
        var second = await ToolLauncher.TestAcquireAsync("-s R12345678 cmd", waitMs: 50);
        second.Should().BeNull("same-device commands must serialize");

        first!.Dispose();
        var after = await ToolLauncher.TestAcquireAsync("-s R12345678 cmd", waitMs: 5000);
        after.Should().NotBeNull("gate must release when first command completes");
        after!.Dispose();
    }

    [Fact]
    public async Task QueueDeadline_DoesNotLaunchAndReleasesGlobalCapacity()
    {
        using var first = await ToolLauncher.TestAcquireAsync("-s DEEP_GATE_TEST cmd");
        var result = await ToolLauncher.RunAsync("nonexistent-deep-link-test-executable", "-s DEEP_GATE_TEST shell am start",
            hidePayloadInLogs: true, gateTimeoutMs: 50);
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Device queue timed out.");
        first!.Dispose();
        using var after = await ToolLauncher.TestAcquireAsync("-s DEEP_GATE_TEST cmd", waitMs: 5000);
        after.Should().NotBeNull();
    }

    [Fact]
    public async Task DifferentDevices_DoNotBlockEachOther()
    {
        var first = await ToolLauncher.TestAcquireAsync("-s DEVICE_A cmd");
        var second = await ToolLauncher.TestAcquireAsync("-s DEVICE_B cmd", waitMs: 5000);
        second.Should().NotBeNull("different devices must run in parallel");
        first!.Dispose();
        second!.Dispose();
    }

    [Fact]
    public async Task NoDeviceKey_UsesGlobalCapOnly()
    {
        var first = await ToolLauncher.TestAcquireAsync("version");
        first.Should().NotBeNull();
        first!.Dispose();
    }
}
