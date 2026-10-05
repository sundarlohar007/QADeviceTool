using LogPro.Models;

namespace LogPro.Tests.Services;

public class UpdateInfoTests
{
    [Theory]
    [InlineData("3.4.0", "3.3.0", false)]
    [InlineData("3.4.0", "3.4.0", false)]
    [InlineData("3.4.0", "3.5.0", true)]
    [InlineData("unknown", "3.5.0", false)]
    [InlineData("v3.4.0", "v3.5.0", true)]
    public void IsNewerAvailable_RequiresHigherParsedVersion(string current, string latest, bool expected)
    {
        new UpdateInfo { CurrentVersion = current, LatestVersion = latest }
            .IsNewerAvailable.Should().Be(expected);
    }

    [Fact]
    public void IsInstallable_AllowsVerifiedLatestWhenInstalledVersionIsUnknown()
    {
        var update = new UpdateInfo
        {
            CurrentVersion = "unknown",
            LatestVersion = "3.5.0",
            DownloadUrl = "https://github.com/example/release.exe",
            Sha256 = new string('a', 64)
        };
        update.IsNewerAvailable.Should().BeFalse();
        update.IsInstallable.Should().BeTrue();
    }
}
