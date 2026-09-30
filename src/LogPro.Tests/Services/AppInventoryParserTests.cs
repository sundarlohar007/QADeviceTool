using LogPro.Helpers;
using LogPro.Models;
using LogPro.Services;

namespace LogPro.Tests.Services;

public class AppInventoryParserTests
{
    [Fact]
    public void AndroidPackageList_UsesExplicitCategoryAndRejectsNoise()
    {
        var apps = AdbService.ParsePackageList("package:com.example.one versionCode:123\r\nwarning\r\npackage:com.example.two\n", AppCategory.System);
        apps.Should().HaveCount(2);
        apps.Should().OnlyContain(a => a.Category == AppCategory.System && a.Platform == DevicePlatform.Android);
        apps.Should().Contain(a => a.PackageId == "com.example.one" && a.Version == "code 123");
    }

    [Fact]
    public void IosBundleIds_AllowHyphensWithoutRelaxingAndroidPackageRules()
    {
        SecurityHelper.IsValidBundleId("com.example.my-app").Should().BeTrue();
        SecurityHelper.IsValidPackageName("com.example.my-app").Should().BeFalse();
        SecurityHelper.IsValidBundleId("com.example.app;rm").Should().BeFalse();
    }

    [Fact]
    public void IosAppsList_PreservesRequestedCategory()
    {
        var apps = IosService.ParseAppsList("{\"com.example.app\":{\"CFBundleName\":\"App\"}}", AppCategory.Hidden);
        apps.Should().ContainSingle(a => a.Category == AppCategory.Hidden);
    }
}
