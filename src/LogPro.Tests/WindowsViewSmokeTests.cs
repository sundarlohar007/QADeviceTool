using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace LogPro.Tests;

[Collection("HeavyE2E")]
public class WindowsViewSmokeTests
{
    [Fact]
    public void EveryTab_LoadsAndResizesWithBothThemes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    app.Resources.MergedDictionaries.Clear();
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Themes", theme + "Theme.xaml"), UriKind.Absolute)
                    });
                    var views = new UserControl[]
                    {
                        new LogPro.Views.DashboardView(), new LogPro.Views.SessionView(),
                        new LogPro.Views.DeviceView(), new LogPro.Views.AppManagementView(),
                        new LogPro.Views.FileExplorerView(), new LogPro.Views.ShellView(),
                        new LogPro.Views.DeepLinkView(), new LogPro.Views.VitalsView(),
                        new LogPro.Views.MacroView(), new LogPro.Views.StressTestView(),
                        new LogPro.Views.PerformanceView(), new LogPro.Views.SettingsView()
                    };
                    foreach (var view in views)
                        foreach (var size in new[] { new Size(720, 530), new Size(1060, 730), new Size(1700, 1000) })
                        {
                            view.Measure(size);
                            view.Arrange(new Rect(size));
                            view.UpdateLayout();
                            double.IsFinite(view.ActualWidth).Should().BeTrue(view.GetType().Name);
                            view.ActualWidth.Should().BeGreaterThan(0);
                        }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
        failure.Should().BeNull();
    }
}
