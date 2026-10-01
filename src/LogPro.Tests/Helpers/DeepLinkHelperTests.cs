using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.Tests.Helpers;

public class DeepLinkHelperTests
{
    [Theory]
    [InlineData("intent://host/path#Intent;scheme=https;end")]
    [InlineData("intent://host/path#Intent;scheme=h%74tps;end")]
    [InlineData("intent://host/path#Intent;scheme=file;end")]
    [InlineData("intent://host/path#Intent;scheme=data;end")]
    [InlineData("intent://host/path#Intent;scheme=myapp;SEL;scheme=https;end")]
    [InlineData("android-app://com.example.app/https/example.test")]
    [InlineData("intent://host")]
    [InlineData("intent://host#Intent;scheme=myapp;scheme=https;end")]
    [InlineData("intent://host#Intent;scheme=myapp;S.extra=%0Asecret;end")]
    [InlineData("myapp://host/%ZZ")]
    [InlineData("myapp://host/$(id)")]
    [InlineData("myapp://host/`id`")]
    [InlineData("myapp://host\n")]
    [InlineData("myapp://host\0x")]
    [InlineData("intent://host#Intent;scheme=myapp;i.count=not-a-number;end")]
    [InlineData("intent://host#Intent;scheme=myapp;B.enabled=not-a-boolean;end")]
    [InlineData("intent://host#Intent;scheme=myapp;launchFlags=0x123456789ABC;end")]
    public void InvalidAndNetworkIntents_AreRejectedAtBothBoundaries(string value)
    {
        DeepLinkHelper.TryValidate(value, out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
        SecurityHelper.IsOfflineSafeUri(value).Should().BeFalse();
        AdbService.TryBuildDeepLinkIntentArgs("device-1", value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("myapp://page?note=file:example")]
    [InlineData("myapp://orders/123?source=qa")]
    [InlineData("intent://scan/#Intent;scheme=zxing;package=com.example.app;S.source=qa;end")]
    [InlineData("intent:#Intent;package=com.example.app;end")]
    [InlineData("intent:#Intent;end")]
    public void SafeLinks_AreAccepted(string value) => DeepLinkHelper.TryValidate(value, out _).Should().BeTrue();

    [Theory]
    [InlineData("Starting: Intent { dat=myapp://secret }\nError: Activity not started, unable to resolve Intent", DeepLinkOutcome.Failed)]
    [InlineData("Starting: Intent\nStatus: timeout\nComplete", DeepLinkOutcome.TimedOut)]
    [InlineData("Starting: Intent\nStatus: ok\nComplete", DeepLinkOutcome.Launched)]
    [InlineData("Starting: Intent { dat=myapp://cancelled/timed out/delivered to }\nStatus: ok\nComplete", DeepLinkOutcome.Launched)]
    [InlineData("Warning: Activity not started, intent has been delivered to currently running top-most instance.\nStatus: ok\nComplete", DeepLinkOutcome.Delivered)]
    [InlineData("Warning: Activity not started, its current task has been brought to the front\nStatus: ok\nComplete", DeepLinkOutcome.TaskBroughtForward)]
    [InlineData("Starting: Intent\nComplete", DeepLinkOutcome.Failed)]
    [InlineData("Warning: Activity not started because intent should be handled by the caller\nStatus: ok", DeepLinkOutcome.Deferred)]
    public void LaunchParsing_UsesActualOutcomeInsteadOfStartingMarker(string output, DeepLinkOutcome expected)
    {
        var result = DeepLinkHelper.ParseResult(new() { Success = true, ExitCode = 0, Output = output });
        result.Outcome.Should().Be(expected);
        result.Message.Should().NotContain("secret");
    }

    [Fact]
    public void LaunchParsing_ExtractsTimingAndActivityAndRejectsStderrErrors()
    {
        var output = "Starting: Intent\nStatus: ok\nActivity: com.example.app/.MainActivity\nTotalTime: 320\nWaitTime: 330\nComplete";
        var result = DeepLinkHelper.ParseResult(new() { Success = true, Output = output });
        result.Activity.Should().Be("com.example.app/.MainActivity");
        result.TotalTimeMs.Should().Be(320);
        result.WaitTimeMs.Should().Be(330);
        DeepLinkHelper.ParseResult(new() { Success = true, Output = output, Error = "Error: permission denied myapp://secret" }).Success.Should().BeFalse();
        DeepLinkHelper.ParseResult(new() { Error = "Process cancelled." }).Outcome.Should().Be(DeepLinkOutcome.Cancelled);
    }

    [Fact]
    public void PreviewAndLabels_HidePayloadUnlessExplicitlyRevealed()
    {
        const string link = "intent://orders/secret-customer#Intent;scheme=myapp;S.customer=secret-person;end";
        DeepLinkHelper.Preview(link, false).Should().NotContain("secret");
        DeepLinkHelper.Preview(link, true).Should().Contain("secret-person").And.Contain("secret-customer");
        DeepLinkHelper.SafeLabel(link).Should().Be("intent:[destination hidden]");
        DeepLinkHelper.TryValidate("myapp:" + new string('x', 8193), out _).Should().BeFalse();
    }

    [Fact]
    public void Arguments_PreserveHostAndRemoteQuotesAndConstrainTarget()
    {
        const string link = "myapp://orders/O'Brien?note=\"quoted\"&locale=日本語";
        AdbService.TryBuildDeepLinkArgs("device-1", link, new("com.example.app", true), false, out var args).Should().BeTrue();
        args.Should().Be("-s device-1 shell " + ToolLauncher.QuoteArgument("am start -W --user current -c android.intent.category.BROWSABLE -p com.example.app -a android.intent.action.VIEW -d 'myapp://orders/O'\\''Brien?note=\"quoted\"&locale=日本語'"));
        AdbService.TryBuildDeepLinkArgs("device-1", "intent://page#Intent;scheme=myapp;package=com.other.app;end", new("com.example.app"), false, out _).Should().BeFalse();
        AdbService.TryBuildDeepLinkArgs("device-1", "intent://page#Intent;scheme=myapp;end", new("com.example.app"), true, out var inspect).Should().BeTrue();
        inspect.Should().Contain("query-activities --brief --components --user current").And.Contain("package=com.example.app;end");
        AdbService.TryBuildDeepLinkArgs("192.0.2.1:5555", link, new(), false, out _).Should().BeFalse();
        AdbService.TryBuildDeepLinkArgs("device-1", link, new("com.app;id"), false, out _).Should().BeFalse();
    }
}
