using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LogPro.Avalonia.Views;

public partial class ShellView : UserControl
{
    public ShellView() => InitializeComponent();

    private void CommandInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not LogPro.ViewModels.ShellViewModel vm ||
            !vm.ExecuteCommandCommand.CanExecute(null)) return;
        vm.ExecuteCommandCommand.Execute(null);
        e.Handled = true;
    }

    private void OutputTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is LogPro.ViewModels.ShellViewModel { FollowTail: true, IsOutputPaused: false })
            OutputTextBox.CaretIndex = OutputTextBox.Text?.Length ?? 0;
    }

    private void FindNext_Click(object? sender, RoutedEventArgs e)
    {
        var query = SearchTextBox.Text;
        if (string.IsNullOrWhiteSpace(query)) return;
        var text = OutputTextBox.Text ?? string.Empty;
        var start = Math.Min(OutputTextBox.SelectionEnd, text.Length);
        var index = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            if (DataContext is LogPro.ViewModels.ShellViewModel missingVm)
                missingVm.StatusMessage = "No match in visible output.";
            return;
        }
        OutputTextBox.Focus();
        OutputTextBox.SelectionStart = index;
        OutputTextBox.SelectionEnd = index + query.Length;
        if (DataContext is LogPro.ViewModels.ShellViewModel vm)
            vm.StatusMessage = $"Match at character {index + 1}.";
    }
}
