using System.Windows.Controls;
using System.Windows;
using LogPro.ViewModels;

namespace LogPro.Views;

public partial class AppManagementView : UserControl
{
    public AppManagementView()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is AppManagementViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await vm.InstallFilesAsync(paths);
        e.Handled = true;
    }
}
