using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Services;

/// <summary>Avalonia implementations of the host UI services (§4.1).</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (Owner == null) return false;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new global::Avalonia.Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new global::Avalonia.Thickness(0, 16, 0, 0),
            Spacing = 8
        };
        var yes = new Button { Content = "Yes" };
        yes.Click += (_, _) => window.Close(true);
        var no = new Button { Content = "No" };
        no.Click += (_, _) => window.Close(false);
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        panel.Children.Add(buttons);
        window.Content = panel;
        return await window.ShowDialog<bool>(Owner);
    }

    public bool Confirm(string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var result = false;
        var panel = new StackPanel { Margin = new global::Avalonia.Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Margin = new global::Avalonia.Thickness(0, 16, 0, 0), Spacing = 8 };
        var ok = new Button { Content = "Yes" };
        ok.Click += (_, _) => { result = true; window.Close(); };
        var cancel = new Button { Content = "No" };
        cancel.Click += (_, _) => window.Close();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog(Owner!);
        return result;
    }

    public void Info(string title, string message) => ShowMessage(title, message);
    public void Error(string title, string message) => ShowMessage(title, message);

    private static void ShowMessage(string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new global::Avalonia.Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
        var ok = new Button { Content = "OK", HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Margin = new global::Avalonia.Thickness(0, 16, 0, 0) };
        ok.Click += (_, _) => window.Close();
        panel.Children.Add(ok);
        window.Content = panel;
        window.ShowDialog(Owner!);
    }

    public static Window? Owner { get; set; }
}

public sealed class AvaloniaFileDialogService : IFileDialogService
{
    public string? OpenFile(string title, string filter) => null;
    public string? SaveFile(string title, string filter, string defaultFileName) => null;
    public string? OpenFolder(string title) => null;

    public async Task<string?> OpenFileAsync(string title, string filter)
    {
        var owner = AvaloniaDialogService.Owner;
        if (owner == null) return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string filter, string defaultFileName)
    {
        var owner = AvaloniaDialogService.Owner;
        if (owner == null) return null;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = title, SuggestedFileName = defaultFileName });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        var owner = AvaloniaDialogService.Owner;
        if (owner == null) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}

public sealed class AvaloniaClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        try
        {
            var top = AvaloniaDialogService.Owner != null
                ? global::Avalonia.Controls.TopLevel.GetTopLevel(AvaloniaDialogService.Owner)
                : null;
            if (top?.Clipboard == null) return;

            var data = new global::Avalonia.Input.DataTransfer();
            data.Add(global::Avalonia.Input.DataTransferItem.CreateText(text));
            top.Clipboard.SetDataAsync(data).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogPro.Services.AppLogger.Log.Debug(ex, "[AvaloniaUi] Clipboard set failed");
        }
    }
}
