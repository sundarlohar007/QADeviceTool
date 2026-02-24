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
            if (_vm != null)
            {
                _vm.ScrollToEndRequested -= OnScrollToEndRequested;
            }

            _vm = DataContext as SessionViewModel;
            if (_vm != null)
            {
                _vm.ScrollToEndRequested += OnScrollToEndRequested;
            }
        };
    }

    private void OnScrollToEndRequested()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (LogDataGrid.Items.Count > 0)
            {
                var lastItem = LogDataGrid.Items[LogDataGrid.Items.Count - 1];
                LogDataGrid.ScrollIntoView(lastItem);
            }
        });
    }
}
