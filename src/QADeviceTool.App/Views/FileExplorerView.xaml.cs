using System.Windows.Controls;
using System.Windows.Input;
using QADeviceTool.Models;
using QADeviceTool.ViewModels;

namespace QADeviceTool.Views;

public partial class FileExplorerView : UserControl
{
    public FileExplorerView()
    {
        InitializeComponent();
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && 
            grid.SelectedItem is DeviceFile selectedFile && 
            DataContext is FileExplorerViewModel vm)
        {
            if (vm.ItemDoubleClickedCommand.CanExecute(selectedFile))
            {
                vm.ItemDoubleClickedCommand.Execute(selectedFile);
            }
        }
    }
}
