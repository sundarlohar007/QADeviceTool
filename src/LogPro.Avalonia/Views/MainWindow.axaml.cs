using Avalonia.Controls;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as MainViewModel)?.Dispose();
    }
}
