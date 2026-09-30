using System.Windows.Controls;

namespace LogPro.Views;

public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();
    }

    private void CommandInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter ||
            DataContext is not LogPro.ViewModels.ShellViewModel vm ||
            !vm.ExecuteCommandCommand.CanExecute(null)) return;
        vm.ExecuteCommandCommand.Execute(null);
        e.Handled = true;
    }

    private void OutputTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb &&
            DataContext is LogPro.ViewModels.ShellViewModel { FollowTail: true, IsOutputPaused: false })
            tb.ScrollToEnd();
    }

    private void FindNext_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var query = SearchTextBox.Text;
        if (string.IsNullOrWhiteSpace(query)) return;
        var text = OutputTextBox.Text;
        var start = Math.Min(OutputTextBox.SelectionStart + OutputTextBox.SelectionLength, text.Length);
        var index = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            if (DataContext is LogPro.ViewModels.ShellViewModel missingVm)
                missingVm.StatusMessage = "No match in visible output.";
            return;
        }
        OutputTextBox.Focus();
        OutputTextBox.Select(index, query.Length);
        if (DataContext is LogPro.ViewModels.ShellViewModel vm)
            vm.StatusMessage = $"Match at character {index + 1}.";
    }
}

