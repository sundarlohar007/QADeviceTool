using FluentAssertions;
using LogPro.Models;

namespace LogPro.Tests.Models;

public class DeviceInfoTests
{
    [Fact]
    public void DisplayName_WhenNameIsEmpty_ReturnsModel()
    {
        var device = new DeviceInfo { Name = "", Model = "Pixel 7" };
        device.DisplayName.Should().Be("Pixel 7");
    }

    [Fact]
    public void DisplayName_WhenNameHasValue_ReturnsName()
    {
        var device = new DeviceInfo { Name = "My Phone", Model = "Pixel 7" };
        device.DisplayName.Should().Be("My Phone");
    }

    [Fact]
    public void DisplayName_WhenNameIsWhitespace_ReturnsModel()
    {
        var device = new DeviceInfo { Name = "   ", Model = "Pixel 7" };
        device.DisplayName.Should().Be("Pixel 7");
    }

    [Fact]
    public void DisplayNotes_WhenNotesAreEmpty_ReturnsDefaultText()
    {
        var device = new DeviceInfo { Notes = "" };
        device.DisplayNotes.Should().Be("No notes");
    }

    [Fact]
    public void DisplayNotes_WhenNotesHaveValue_ReturnsNotes()
    {
        var device = new DeviceInfo { Notes = "Test device" };
        device.DisplayNotes.Should().Be("Test device");
    }

    [Fact]
    public void PlatformIcon_WhenAndroid_ReturnsAndroid()
    {
        var device = new DeviceInfo { Platform = DevicePlatform.Android };
        device.PlatformIcon.Should().Be("Android");
    }

    [Fact]
    public void PlatformIcon_WhenIOS_ReturnsIOS()
    {
        var device = new DeviceInfo { Platform = DevicePlatform.iOS };
        device.PlatformIcon.Should().Be("iOS");
    }

    [Theory]
    [InlineData(DeviceConnectionState.Online, "Connected")]
    [InlineData(DeviceConnectionState.Unauthorized, "Unauthorized (Accept RSA)")]
    [InlineData(DeviceConnectionState.PendingTrust, "Trust Dialog Pending")]
    [InlineData(DeviceConnectionState.Offline, "Offline")]
    public void StatusText_ReturnsExpectedText(DeviceConnectionState state, string expected)
    {
        var device = new DeviceInfo { ConnectionState = state };
        device.StatusText.Should().Be(expected);
    }
}

public class DevicePlatformTests
{
    [Fact]
    public void DevicePlatform_HasAndroidMember()
    {
        var value = DevicePlatform.Android;
        value.Should().Be(LogPro.Models.DevicePlatform.Android);
    }

    [Fact]
    public void DevicePlatform_HasIOSMember()
    {
        var value = DevicePlatform.iOS;
        value.Should().Be(LogPro.Models.DevicePlatform.iOS);
    }
}

public class DeviceConnectionStateTests
{
    [Fact]
    public void DeviceConnectionState_HasAllExpectedMembers()
    {
        var values = Enum.GetValues<DeviceConnectionState>();
        values.Should().Contain(DeviceConnectionState.Online);
        values.Should().Contain(DeviceConnectionState.Offline);
        values.Should().Contain(DeviceConnectionState.Unauthorized);
        values.Should().Contain(DeviceConnectionState.PendingTrust);
    }
}