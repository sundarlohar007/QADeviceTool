namespace LogPro.Tests.ViewModels;

public class PerformanceViewResourceTests
{
    [Fact]
    public void WpfPerformanceView_DeclaresItsLocalStaticResources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "LogPro.sln")))
            root = root.Parent;

        root.Should().NotBeNull("the test must run from a repository checkout");
        var path = Path.Combine(root!.FullName, "src", "LogPro.App", "Views", "PerformanceView.xaml");
        var xaml = File.ReadAllText(path);

        xaml.Should().Contain("x:Key=\"Card\"");
        xaml.Should().Contain("x:Key=\"PrimaryBtn\"");
        xaml.Should().Contain("x:Key=\"DangerBtn\"");
    }
}
