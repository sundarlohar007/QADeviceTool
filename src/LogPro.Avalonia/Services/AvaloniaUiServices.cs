using Avalonia.Controls;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Services;

/// <summary>Avalonia implementations of the host UI services (§4.1).</summary>
public sealed class AvaloniaDialogService : IDialogService
{
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
        window.ShowDialog(Owner);
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
        window.ShowDialog(Owner);
    }

    public static Window? Owner { get; set; }
}

public sealed class AvaloniaClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        // Avalonia 12 clipboard uses SetDataAsync(IAsyncDataTransferObject) — wired during view parity.
        // Best-effort noop until then (WPF remains the shipping UI).
    }
}
