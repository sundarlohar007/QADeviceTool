using LogPro.Helpers;

namespace LogPro.Tests.Helpers;

public class SecurityHelperTests
{
    [Theory]
    [InlineData("mygame://launch/level-1", true)]
    [InlineData("logpro://open/session", true)]
    [InlineData("https://example.test/game", false)]
    [InlineData("file:///secret.txt", false)]
    [InlineData("data:text/plain,secret", false)]
    public void OfflineUriPolicy_AllowsOnlyCustomNonNetworkSchemes(string uri, bool expected)
        => SecurityHelper.IsOfflineSafeUri(uri).Should().Be(expected);

    [Theory]
    [InlineData("RF8M1234ABCD", true)]
    [InlineData("emulator-5554", true)]
    [InlineData("192.0.2.10:5555", false)]
    [InlineData("\\\\server\\share", false)]
    public void OfflineDeviceSelectorPolicy_RejectsNetworkSelectors(string selector, bool expected)
        => SecurityHelper.IsValidOfflineDeviceSelector(selector).Should().Be(expected);

    [Fact]
    public void NetworkCommandPolicy_RejectsTransportAndExfiltrationCommands()
    {
        SecurityHelper.IsNetworkCapableCommand("connect 192.0.2.10:5555").Should().BeTrue();
        SecurityHelper.IsNetworkCapableCommand("shell curl https://example.test").Should().BeTrue();
        SecurityHelper.IsNetworkCapableCommand("shell dumpsys meminfo com.example.game").Should().BeFalse();
    }

    [Fact]
    public void Redaction_RemovesCredentialsIdentifiersUrlsAndLocalUser()
    {
        var input = "Authorization: Bearer abc123 token=secret123 serial=RF8M1234ABCD " +
                    "https://example.test/path?key=private 192.168.1.42 C:\\Users\\Alice\\capture.txt";

        var redacted = SecurityHelper.RedactSensitiveText(input);

        redacted.Should().NotContain("abc123");
        redacted.Should().NotContain("secret123");
        redacted.Should().NotContain("RF8M1234ABCD");
        redacted.Should().NotContain("https://example.test");
        redacted.Should().NotContain("192.168.1.42");
        redacted.Should().NotContain("Alice");
    }

    [Fact]
    public void ReadOnlyCommandPolicy_RejectsShellCompositionAndWrites()
    {
        SecurityHelper.IsOfflineSafeReadOnlyCommand("shell dumpsys meminfo").Should().BeTrue();
        SecurityHelper.IsOfflineSafeReadOnlyCommand("shell dumpsys meminfo; curl https://example.test").Should().BeFalse();
        SecurityHelper.IsOfflineSafeReadOnlyCommand("shell rm -rf /data").Should().BeFalse();
    }
}
