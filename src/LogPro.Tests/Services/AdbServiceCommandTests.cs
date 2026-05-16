using LogPro.Services;

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
        args.Should().Contain("-s emulator-5554 shell am start -W");
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
        args.Should().Be("-s device-1 shell am start -W -a android.intent.action.VIEW -d 'myapp://orders/123?source=qa'");
    }

    [Fact]
    public void BuildDeepLinkIntentArgs_RejectsShellSubstitution()
    {
        var success = AdbService.TryBuildDeepLinkIntentArgs(
            "device-1",
            "https://example.com/$(id)",
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
}
