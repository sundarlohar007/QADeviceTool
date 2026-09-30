using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Views;

public partial class AppManagementView : UserControl
{
    public AppManagementView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not AppManagementViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        var paths = files?.Select(file => file.TryGetLocalPath())
            .OfType<string>().ToArray();
        if (paths is { Length: > 0 }) await vm.InstallFilesAsync(paths);
        e.Handled = true;
    }
}
