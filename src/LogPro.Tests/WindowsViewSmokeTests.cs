using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using LogPro.Services;
using LogPro.Models;
using LogPro.ViewModels;
using Moq;

namespace LogPro.Tests;

[Collection("HeavyE2E")]
public class WindowsViewSmokeTests(Xunit.Abstractions.ITestOutputHelper output)
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
                using var preferences = new IsolatedPreferences();
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
                    var monitor = new Moq.Mock<LogPro.Services.IDeviceMonitorService>();
                    var device = new LogPro.Models.DeviceInfo { Serial = "synthetic", Name = "Synthetic Device", Platform = LogPro.Models.DevicePlatform.Android };
                    monitor.Setup(m => m.CurrentDevices).Returns(new List<LogPro.Models.DeviceInfo> { device });
                    var sessions = new Moq.Mock<LogPro.Services.ISessionService>();
                    sessions.Setup(m => m.ActiveSessions).Returns(new List<LogPro.Models.LogSession>());
                    sessions.Setup(m => m.GetSavedSessions()).Returns(new List<LogPro.Models.LogSession>());
                    using var sessionVm = new LogPro.ViewModels.SessionViewModel(sessions.Object,
                        new Moq.Mock<LogPro.Services.IAdbService>().Object, new Moq.Mock<LogPro.Services.IIosService>().Object,
                        monitor.Object, new LogPro.Services.WpfUiDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                    var sessionView = views.OfType<LogPro.Views.SessionView>().Single();
                    sessionView.DataContext = sessionVm;
                    sessionVm.LogEntriesView.AddRange(Enumerable.Range(1, 20000).Select(i => new LogPro.Models.LogEntry
                    { Message = "Synthetic populated row " + i, RawLine = "row " + i }));
                    using var store = new LogPro.Services.DeviceStore(new LogPro.Tests.ViewModels.ImmediateUiDispatcher());
                    store.UpdateDevices(new[] { device });
                    using var profiler = new LogPro.ViewModels.ProfilerViewModel(new Moq.Mock<LogPro.Services.IAdbService>().Object,
                        store, new LogPro.Tests.ViewModels.ImmediateUiDispatcher());
                    var performanceView = views.OfType<LogPro.Views.PerformanceView>().Single();
                    performanceView.DataContext = profiler;
                    var adb = new Moq.Mock<IAdbService>();
                    var ios = new Moq.Mock<IIosService>();
                    var mirror = new Moq.Mock<IScrcpyService>();
                    adb.Setup(x => x.CheckAvailabilityAsync()).ReturnsAsync(new ToolStatus { IsInstalled = true });
                    ios.Setup(x => x.CheckAvailabilityAsync()).ReturnsAsync(new ToolStatus { IsInstalled = true });
                    mirror.Setup(x => x.CheckAvailabilityAsync()).ReturnsAsync(new ToolStatus { IsInstalled = true });
                    adb.Setup(x => x.GetAppInventoryAsync(Moq.It.IsAny<string>())).ReturnsAsync(new AppInventoryResult(true,
                        new[] { new AppItem { PackageId = "com.example.app", Name = "Localized sample application", Category = AppCategory.User } }));
                    adb.Setup(x => x.ListDirectoryAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<string>())).ReturnsAsync(
                        new List<DeviceFile> { new() { Name = "sample.txt", Path = "/sdcard/sample.txt" } });
                    using var services = new ServiceCollection()
                        .AddSingleton<IUiDispatcher>(new LogPro.Services.WpfUiDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher))
                        .AddSingleton<IDeviceStore, DeviceStore>()
                        .AddSingleton(adb.Object).AddSingleton(ios.Object).AddSingleton(mirror.Object)
                        .AddSingleton(sessions.Object).AddSingleton(monitor.Object)
                        .AddSingleton(new DependencyChecker(adb.Object, ios.Object, mirror.Object)).BuildServiceProvider();
                    using var main = new MainViewModel(services);
                    views[0].DataContext = main.DashboardVM;
                    views[2].DataContext = main.DeviceVM;
                    views[3].DataContext = main.AppManagementVM;
                    views[4].DataContext = main.FileExplorerVM;
                    views[5].DataContext = main.ShellVM;
                    views[6].DataContext = main.DeepLinkVM;
                    views[7].DataContext = main.VitalsVM;
                    views[8].DataContext = main.MacroVM;
                    views[9].DataContext = main.StressTestVM;
                    views[11].DataContext = main.SettingsVM;
                    main.AppManagementVM.InstalledApps.Add(new AppItem { Name = "Sample application", PackageId = "com.example.app" });
                    main.FileExplorerVM.Files.Add(new DeviceFile { Name = "sample.txt", Path = "/sdcard/sample.txt" });
                    main.DeepLinkVM.TargetUrl = "https://example.test/a/long/path";
                    PumpDispatcher();
                    foreach (var view in views)
                        view.DataContext.Should().NotBeNull(view.GetType().Name);
                    foreach (var view in views)
                        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                            foreach (var size in new[] { new Size(720, 530), new Size(1060, 730), new Size(1700, 1000) })
                            {
                                view.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                                view.Measure(size);
                                view.Arrange(new Rect(size));
                                view.UpdateLayout();
                                double.IsFinite(view.ActualWidth).Should().BeTrue(view.GetType().Name);
                                view.ActualWidth.Should().BeGreaterThan(0);
                                view.LayoutTransform = System.Windows.Media.Transform.Identity;
                            }
                    var changed = device.WithTemporaryUnavailable(false);
                    changed.BatteryLevel = "70%";
                    store.UpdateDevices(new[] { changed });
                    performanceView.UpdateLayout();
                    store.SelectedDevice?.Serial.Should().Be("synthetic", "a populated ComboBox must not clear global selection during refresh");
                    var list = (ListBox)sessionView.FindName("LogList");
                    list.Items.Count.Should().Be(20000);
                    list.ItemContainerGenerator.ContainerFromIndex(19999).Should().BeNull("offscreen rows must stay virtualized");
                    var resets = 0;
                    sessionVm.LogEntriesView.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
                    var appendTime = System.Diagnostics.Stopwatch.StartNew();
                    sessionVm.LogEntriesView.AddRange(Enumerable.Range(1, 500).Select(i => new LogPro.Models.LogEntry { Message = "new row " + i }));
                    sessionView.UpdateLayout();
                    resets.Should().Be(0, "live appends must not rebuild 20,000 existing rows");
                    appendTime.Stop();
                    output.WriteLine($"{theme}: 500-row append/layout into 20,000 rows: {appendTime.ElapsedMilliseconds} ms");
                    appendTime.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
                    sessionVm.LogEntries.AddRange(sessionVm.LogEntriesView);
                    var filterTime = System.Diagnostics.Stopwatch.StartNew();
                    sessionVm.LogLevelFilters.Single(f => f.Level == LogLevel.Unknown).IsSelected = false;
                    while (!sessionVm.FilteringTask.IsCompleted && filterTime.Elapsed < TimeSpan.FromSeconds(5))
                    { PumpDispatcher(); Thread.Sleep(1); }
                    sessionVm.FilteringTask.IsCompletedSuccessfully.Should().BeTrue();
                    sessionVm.LogEntriesView.Should().BeEmpty();
                    output.WriteLine($"{theme}: background filter and WPF apply for 20,500 rows: {filterTime.ElapsedMilliseconds} ms");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(45)).Should().BeTrue();
        failure.Should().BeNull();
    }
    private static void PumpDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

}
