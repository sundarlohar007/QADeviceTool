using LogPro.Services;
using LogPro.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using LogPro.Avalonia.Services;

namespace LogPro.Avalonia;

/// <summary>
/// Avalonia composition root — mirrors the WPF one (App.CreateMainViewModel) but registers
/// Avalonia adapters. The same MainViewModel + 11 child VMs drive both front-ends (§4.1).
/// </summary>
public static class CompositionRoot
{
    public static MainViewModel CreateMainViewModel()
    {
        var services = new ServiceCollection()
            .AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>()
            .AddSingleton<IDeviceStore, DeviceStore>()
            .AddSingleton<IAdbService, AdbService>()
            .AddSingleton<IIosService, IosService>()
            .AddSingleton<IScrcpyService, ScrcpyService>()
            .AddSingleton<ISessionService>(sp => new SessionService(
                sp.GetRequiredService<IAdbService>(),
                sp.GetRequiredService<IIosService>()))
            .AddSingleton<IDeviceMonitorService>(sp => new DeviceMonitorService(
                sp.GetRequiredService<IAdbService>(),
                sp.GetRequiredService<IIosService>()))
            .AddSingleton(sp => new DependencyChecker(
                sp.GetRequiredService<IAdbService>(),
                sp.GetRequiredService<IIosService>(),
                sp.GetRequiredService<IScrcpyService>()))
            .AddSingleton<MainViewModel>()
            .BuildServiceProvider();

        return services.GetRequiredService<MainViewModel>();
    }
}
