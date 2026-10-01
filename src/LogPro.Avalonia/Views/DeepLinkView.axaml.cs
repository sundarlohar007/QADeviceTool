using Avalonia.Controls;
using Avalonia.Input;

namespace LogPro.Avalonia.Views;

public partial class DeepLinkView : UserControl
{
    public DeepLinkView() => InitializeComponent();
    private void TargetUrl_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not LogPro.ViewModels.DeepLinkViewModel vm ||
            !vm.FireIntentCommand.CanExecute(null)) return;
        vm.FireIntentCommand.Execute(null);
        e.Handled = true;
    }
}
