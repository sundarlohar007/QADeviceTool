using Avalonia.Controls;
using Avalonia.Interactivity;
using LogPro.Models;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Views;

public partial class FileExplorerView : UserControl
{
    public FileExplorerView() => InitializeComponent();

    private void Files_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: DeviceFile file } && DataContext is FileExplorerViewModel vm &&
            vm.ItemDoubleClickedCommand.CanExecute(file))
            vm.ItemDoubleClickedCommand.Execute(file);
    }

    private void Breadcrumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileBreadcrumb crumb } && DataContext is FileExplorerViewModel vm &&
            vm.NavigateBreadcrumbCommand.CanExecute(crumb))
            vm.NavigateBreadcrumbCommand.Execute(crumb);
    }
}
