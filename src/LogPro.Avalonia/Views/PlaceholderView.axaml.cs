using Avalonia;
using Avalonia.Controls;

namespace LogPro.Avalonia.Views;

public partial class PlaceholderView : UserControl
{
    public PlaceholderView() => InitializeComponent();

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PlaceholderView, string>(nameof(Title), defaultValue: "View");

    public string Title
    {
        get => (string?)GetValue(TitleProperty) ?? string.Empty;
        set => SetValue(TitleProperty, value);
    }
}
