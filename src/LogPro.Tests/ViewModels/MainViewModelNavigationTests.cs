using LogPro.Services;
using LogPro.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LogPro.Tests.ViewModels;

/// <summary>
/// Navigation is wired through the composition root — this proves the full
/// MainViewModel graph (11 child VMs) constructs headlessly and that every
/// sidebar destination switches CurrentView.
/// </summary>
public class MainViewModelNavigationTests
{
    private static MainViewModel CreateVm()
    {
        var services = new ServiceCollection()
            .AddSingleton<IUiDispatcher, ImmediateUiDispatcher>()
            .AddSingleton<IDeviceStore, DeviceStore>()
            .AddSingleton<IAdbService, AdbService>()
            .AddSingleton<IIosService, IosService>()
            .AddSingleton<IScrcpyService, ScrcpyService>()
            .AddSingleton<ISessionService>(sp => new SessionService(
                sp.GetRequiredService<IAdbService>(), sp.GetRequiredService<IIosService>()))
            .AddSingleton<IDeviceMonitorService>(sp => new DeviceMonitorService(
                sp.GetRequiredService<IAdbService>(), sp.GetRequiredService<IIosService>()))
            .AddSingleton(sp => new DependencyChecker(
                sp.GetRequiredService<IAdbService>(), sp.GetRequiredService<IIosService>(), sp.GetRequiredService<IScrcpyService>()))
            .AddSingleton<MainViewModel>()
            .BuildServiceProvider();

        return services.GetRequiredService<MainViewModel>();
    }

    [Fact]
    public void DefaultView_IsDashboard()
    {
        using var vm = CreateVm();
        vm.CurrentView.Should().Be(vm.DashboardVM);
    }

    [Theory]
    [InlineData("dashboard", typeof(DashboardViewModel))]
    [InlineData("sessions", typeof(SessionViewModel))]
    [InlineData("device", typeof(DeviceViewModel))]
    [InlineData("devices", typeof(DeviceViewModel))]
    [InlineData("apps", typeof(AppManagementViewModel))]
    [InlineData("shell", typeof(ShellViewModel))]
    [InlineData("deeplink", typeof(DeepLinkViewModel))]
    [InlineData("vitals", typeof(VitalsViewModel))]
    [InlineData("files", typeof(FileExplorerViewModel))]
    [InlineData("macros", typeof(MacroViewModel))]
    [InlineData("StressTest", typeof(StressTestViewModel))]
    [InlineData("Settings", typeof(SettingsViewModel))]
    public void Navigate_SwitchesCurrentView(string destination, Type expectedType)
    {
        using var vm = CreateVm();
        vm.Navigate(destination);
        vm.CurrentView.Should().BeOfType(expectedType, because: $"navigating to '{destination}' must show the right view");
    }

    [Fact]
    public void Navigate_UnknownDestination_FallsBackToDashboard()
    {
        using var vm = CreateVm();
        vm.Navigate("nonexistent-view");
        vm.CurrentView.Should().Be(vm.DashboardVM);
    }

    [Fact]
    public void Navigate_PreservesSelectionViaSelectedNavItem()
    {
        using var vm = CreateVm();
        vm.Navigate("shell");
        vm.SelectedNavItem.Should().Be("shell");
    }
}
