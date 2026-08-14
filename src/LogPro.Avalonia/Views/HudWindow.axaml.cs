using Avalonia.Controls;

namespace LogPro.Avalonia.Views;

public partial class HudWindow : Window
{
    public HudWindow() => InitializeComponent();

    private void OnCloseClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
