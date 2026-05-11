using LogPro.Models;
using LogPro.Services;

namespace LogPro.Tests.Services;

/// <summary>
/// Tests for IosService static parsers using captured pymobiledevice3 fixture text.
/// Parsers are <c>internal static</c>; LogPro.App exposes them via InternalsVisibleTo.
/// </summary>
public class IosServiceParserTests
{
    [Fact]
    public void ParseLockdownInfo_JsonForm_PopulatesAllFields()
    {
        var device = new DeviceInfo();
        var output = """
            {
              "DeviceName": "Test iPhone",
              "ProductType": "iPhone14,2",
              "ProductVersion": "17.4.1",
              "BatteryCurrentCapacity": 87
            }
            """;
        IosService.ParseLockdownInfo(output, device);

        device.Name.Should().Be("Test iPhone");
        device.Model.Should().Be("iPhone14,2");
        device.OsVersion.Should().Be("17.4.1");
        device.BatteryLevel.Should().Be("87%");
    }

    [Fact]
    public void ParseLockdownInfo_PythonDictForm_PopulatesViaRegexFallback()
    {
        var device = new DeviceInfo();
        var output = "{'DeviceName': 'My Phone', 'ProductType': 'iPhone15,3', 'ProductVersion': '18.0', 'BatteryCurrentCapacity': '64'}";
        IosService.ParseLockdownInfo(output, device);

        device.Name.Should().Be("My Phone");
        device.Model.Should().Be("iPhone15,3");
        device.OsVersion.Should().Be("18.0");
    }

    [Fact]
    public void ParseLockdownInfo_EmptyOutput_LeavesDeviceUntouched()
    {
        var device = new DeviceInfo { Name = "kept", Model = "kept" };
        IosService.ParseLockdownInfo("", device);
        device.Name.Should().Be("kept");
        device.Model.Should().Be("kept");
    }

    [Fact]
    public void ParseAppsList_JsonObject_ReturnsAllApps()
    {
        var output = """
            {
              "com.apple.Maps": { "CFBundleDisplayName": "Maps", "CFBundleShortVersionString": "1.0" },
              "com.example.foo": { "CFBundleDisplayName": "Foo", "CFBundleShortVersionString": "2.5" }
            }
            """;
        var apps = IosService.ParseAppsList(output);

        apps.Should().HaveCount(2);
        apps.Should().Contain(a => a.PackageId == "com.apple.Maps" && a.Name == "Maps" && a.Version == "1.0");
        apps.Should().Contain(a => a.PackageId == "com.example.foo" && a.Name == "Foo" && a.Version == "2.5");
        apps.Should().OnlyContain(a => a.Platform == DevicePlatform.iOS);
    }

    [Fact]
    public void ParseAppsList_FallsBackToBundleNameWhenDisplayNameMissing()
    {
        var output = """
            {
              "com.example.bar": { "CFBundleName": "BarApp", "CFBundleVersion": "9" }
            }
            """;
        var apps = IosService.ParseAppsList(output);
        apps.Should().HaveCount(1);
        apps[0].Name.Should().Be("BarApp");
        apps[0].Version.Should().Be("9");
    }

    [Fact]
    public void ParseAppsList_EmptyJsonObject_ReturnsEmpty()
    {
        var apps = IosService.ParseAppsList("{}");
        apps.Should().BeEmpty();
    }

    [Fact]
    public void ParseAfcLs_BasicListing_AssignsDirectoryFlagOnTrailingSlash()
    {
        var output = "DCIM/\nPhotos/\nLibrary\nfile.txt\n";
        var files = IosService.ParseAfcLs(output, "/var/mobile/Media");

        files.Should().HaveCount(4);
        files.Should().Contain(f => f.Name == "DCIM" && f.IsDirectory && f.Path == "/var/mobile/Media/DCIM");
        files.Should().Contain(f => f.Name == "Photos" && f.IsDirectory);
        files.Should().Contain(f => f.Name == "Library" && !f.IsDirectory);
        files.Should().Contain(f => f.Name == "file.txt" && !f.IsDirectory);
    }

    [Fact]
    public void ParseAfcLs_SkipsDotEntries()
    {
        var output = ".\n..\nrealfile\n";
        var files = IosService.ParseAfcLs(output, "/");
        files.Should().HaveCount(1);
        files[0].Name.Should().Be("realfile");
        files[0].Path.Should().Be("/realfile");
    }

    [Fact]
    public void ParseAfcLs_RootPathBuildsCorrectPathString()
    {
        var output = "Photos/\n";
        var files = IosService.ParseAfcLs(output, "/");
        files[0].Path.Should().Be("/Photos");
    }

    [Fact]
    public void ParseAfcLs_EmptyOutput_ReturnsEmpty()
    {
        IosService.ParseAfcLs("", "/").Should().BeEmpty();
    }
}
