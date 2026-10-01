using LogPro.Services;
using LogPro.Helpers;

namespace LogPro.Tests.Services;

public class AdbServiceCommandTests
{
    [Fact]
    public void BuildDeepLinkIntentArgs_AllowsAndroidIntentUri()
    {
        var success = AdbService.TryBuildDeepLinkIntentArgs(
            "emulator-5554",
            "intent://scan/#Intent;scheme=zxing;package=com.google.zxing.client.android;end",
            out var args);

        success.Should().BeTrue();
        args.Should().Contain("-s emulator-5554 shell \"am start -W --user current");
        args.Should().Contain("'intent://scan/#Intent;scheme=zxing;package=com.google.zxing.client.android;end'");
    }

    [Fact]
    public void BuildDeepLinkIntentArgs_UsesViewActionForNormalUris()
    {
        var success = AdbService.TryBuildDeepLinkIntentArgs(
            "device-1",
            "myapp://orders/123?source=qa",
            out var args);

        success.Should().BeTrue();
        args.Should().Be("-s device-1 shell " + ToolLauncher.QuoteArgument("am start -W --user current -a android.intent.action.VIEW -d 'myapp://orders/123?source=qa'"));
    }

    [Fact]
    public void BuildDeepLinkIntentArgs_RejectsShellSubstitution()
    {
        var success = AdbService.TryBuildDeepLinkIntentArgs(
            "device-1",
            "myapp://example/$(id)",
            out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void ParseAndroidLsListing_ParsesToyboxLongOutput()
    {
        var output = """
            total 8
            drwxrwx--x 2 root sdcard_rw 4096 2026-05-11 13:42 DCIM
            -rw-rw---- 1 root sdcard_rw 12 2026-05-11 13:45 report.txt
            lrwxrwxrwx 1 root root 21 2026-05-11 13:46 Pictures -> /storage/emulated/0/Pictures
            """;

        var files = AdbService.ParseAndroidLsListing(output, "/sdcard");

        files.Should().Contain(f => f.Name == "DCIM" && f.Path == "/sdcard/DCIM" && f.IsDirectory);
        files.Should().Contain(f => f.Name == "report.txt" && f.Size == 12 && !f.IsDirectory);
        files.Should().Contain(f => f.Name == "Pictures" && f.Path == "/sdcard/Pictures" && f.IsDirectory);
    }

    [Fact]
    public void ParseSimpleDirectoryListing_ParsesFallbackListing()
    {
        var files = AdbService.ParseSimpleDirectoryListing("DCIM/\nreport.txt\n", "/sdcard/");

        files.Should().HaveCount(2);
        files.Should().Contain(f => f.Name == "DCIM" && f.Path == "/sdcard/DCIM" && f.IsDirectory);
        files.Should().Contain(f => f.Name == "report.txt" && f.Path == "/sdcard/report.txt" && !f.IsDirectory);
    }

    [Theory]
    [InlineData("/", false)]
    [InlineData("/sdcard", false)]
    [InlineData("/storage/emulated/0", false)]
    [InlineData("/system/bin", false)]
    [InlineData("/sdcard/../data/secret", false)]
    [InlineData("/sdcard/照片.png", true)]
    [InlineData("/sdcard/Mom's photos (2026)/a+b.png", true)]
    [InlineData("/data/local/tmp/report.txt", true)]
    public void FileDeletion_IsLimitedToChildrenOfWritableAreas(string path, bool expected)
    {
        AdbService.IsAllowedFileDeletionPath(path).Should().Be(expected);
    }

    [Fact]
    public void NotificationCommand_FollowsAndroidPostContract()
    {
        var supported = AdbService.TryBuildNotificationArgs("device-1", "QA title", "QA body", null, out var args);

        supported.Should().BeTrue();
        args.Should().Contain("cmd notification post -t 'QA title' LogPro_");
        args.Should().EndWith(" 'QA body'");
        args.Should().NotContain("--channel");
        AdbService.TryBuildNotificationArgs("device-1", "title", "body", "custom", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ClipboardMethods_ReportPortableAdbLimitation()
    {
        var adb = new AdbService();

        (await adb.SetDeviceClipboardAsync("device-1", "text")).Should().BeFalse();
        (await adb.GetDeviceClipboardAsync("device-1")).Should().Contain("unavailable");
    }
}
