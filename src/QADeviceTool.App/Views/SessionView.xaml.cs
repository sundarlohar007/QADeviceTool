using System.Windows.Controls;
using System.Windows.Threading;
using QADeviceTool.ViewModels;

namespace QADeviceTool.Views;

public partial class SessionView : UserControl
{
    private SessionViewModel? _vm;

    public SessionView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            // Unwire old VM
            if (_vm != null)
            {
                _vm.LogAppend -= OnLogAppend;
                _vm.LogCleared -= OnLogCleared;
                _vm.LogReplaced -= OnLogReplaced;
            }

            _vm = DataContext as SessionViewModel;
            if (_vm != null)
            {
                _vm.LogAppend += OnLogAppend;
                _vm.LogCleared += OnLogCleared;
                _vm.LogReplaced += OnLogReplaced;
            }
        };
    }

    private void OnLogAppend(string text)
    {
        // Use Background priority so button clicks (Input priority) are processed first
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            LogTextBox.AppendText(text);

            // Trim if too long (keep last ~100K chars)
            if (LogTextBox.Text.Length > 200_000)
            {
                var keep = LogTextBox.Text.Substring(LogTextBox.Text.Length - 100_000);
                LogTextBox.Clear();
                LogTextBox.AppendText("... [earlier log trimmed] ...\n");
                LogTextBox.AppendText(keep);
            }

            LogTextBox.ScrollToEnd();
        });
    }

    private void OnLogCleared()
    {
        Dispatcher.BeginInvoke(() => LogTextBox.Clear());
    }

    private void OnLogReplaced(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogTextBox.Clear();
            LogTextBox.AppendText(text);
            LogTextBox.ScrollToEnd();
        });
    }
}
