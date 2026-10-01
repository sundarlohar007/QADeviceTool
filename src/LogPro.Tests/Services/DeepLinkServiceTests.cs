using LogPro.Models;
using LogPro.Services;
using NLog;
using NLog.Targets;

namespace LogPro.Tests.Services;

[Collection("HeavyE2E")]
public class DeepLinkServiceTests
{
    private static AdbService Service() => new(Path.Combine(AppContext.BaseDirectory, "adb.exe"));

    [Fact]
    public async Task RealProcessBoundary_PreservesQuotesUnicodeAndStructuredResults()
    {
        var result = await Service().LaunchDeepLinkAsync("DEEP_ROUNDTRIP", "myapp://roundtrip/O'Brien?note=\"quoted\"&locale=日本語", new());
        result.Outcome.Should().Be(DeepLinkOutcome.Launched);
        result.Activity.Should().Be("com.example.app/.MainActivity");
        result.TotalTimeMs.Should().Be(120);
        (await Service().BroadcastIntentAsync("DEEP_ROUNDTRIP", "myapp://no-handler")).Should().BeFalse();
    }

    [Fact]
    public async Task HandlerInspection_DistinguishesNoHandlersFromUnsupportedCommands()
    {
        var service = Service();
        var none = await service.InspectDeepLinkAsync("DEEP_INSPECT", "myapp://no-handler", new());
        none.Supported.Should().BeTrue(); none.Handlers.Should().BeEmpty();
        var one = await service.InspectDeepLinkAsync("DEEP_INSPECT", "myapp://page", new());
        one.Handlers.Should().ContainSingle().Which.Should().Be("com.example.app/.MainActivity");
        var unsupported = await service.InspectDeepLinkAsync("DEEP_INSPECT", "myapp://unsupported-inspection", new());
        unsupported.Supported.Should().BeFalse(); unsupported.Message.Should().Contain("unsupported");
    }

    [Fact]
    public async Task LaunchLogs_NeverContainCustomUriPayloadOrExtras()
    {
        _ = AppLogger.Log; // Initialize normal logging before adding a temporary test sink.
        var config = LogManager.Configuration!;
        var memory = new MemoryTarget("deep-link-test") { Layout = "${message}" };
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memory);
        LogManager.ReconfigExistingLoggers();
        try
        {
            var result = await Service().LaunchDeepLinkAsync("DEEP_PRIVACY", "intent://secret-customer/path#Intent;scheme=myapp;S.customer=private-person;end", new());
            result.Success.Should().BeTrue();
            var logs = string.Join("\n", memory.Logs);
            logs.Should().Contain("Deep link payload hidden").And.NotContain("secret-customer").And.NotContain("private-person");
        }
        finally
        {
            config.RemoveRuleByName("deep-link-test");
            foreach (var rule in config.LoggingRules.Where(r => r.Targets.Contains(memory)).ToArray()) config.LoggingRules.Remove(rule);
            config.RemoveTarget("deep-link-test");
            LogManager.ReconfigExistingLoggers();
        }
    }
}
