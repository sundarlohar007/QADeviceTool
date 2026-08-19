using Avalonia.Controls;
using Avalonia.Input;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Views;

public partial class MainWindow : Window
{
    private PaletteWindow? _palette;

    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as MainViewModel)?.Dispose();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control && DataContext is MainViewModel vm)
        {
            TogglePalette(vm);
            e.Handled = true;
        }
    }

    private void TogglePalette(MainViewModel vm)
    {
        if (_palette is { IsVisible: true })
        {
            _palette.Close();
            _palette = null;
            return;
        }

        _palette = PaletteWindow.Create(vm);
        _palette.Closed += (_, _) => _palette = null;
        _palette.Show(this);
    }
}
