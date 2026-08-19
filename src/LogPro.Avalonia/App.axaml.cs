using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LogPro.Avalonia.ViewModels;
using LogPro.Avalonia.Views;

namespace LogPro.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            Services.AvaloniaDialogService.Owner = window;

            LogPro.ViewModels.UiServices.Dispatcher = new Services.AvaloniaUiDispatcher();
            LogPro.ViewModels.UiServices.Dialogs = new Services.AvaloniaDialogService();
            LogPro.ViewModels.UiServices.Clipboard = new Services.AvaloniaClipboardService();
            LogPro.ViewModels.UiServices.Theme = new Services.AvaloniaThemeService();

            window.DataContext = CompositionRoot.CreateMainViewModel();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}