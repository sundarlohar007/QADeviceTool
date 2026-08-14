using Avalonia.Controls;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Views;

public partial class PerformanceView : UserControl
{
    private HudWindow? _hud;

    public PerformanceView() => InitializeComponent();

    private void OnHudClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_hud is { IsVisible: true })
        {
            _hud.Close();
            _hud = null;
            return;
        }
        _hud = new HudWindow { DataContext = DataContext };
        _hud.Show();
    }
}
