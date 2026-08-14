using Avalonia.Controls;
using LogPro.Avalonia.ViewModels;

namespace LogPro.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as ShellViewModel)?.Dispose();
    }
}
