using Avalonia.Controls;
using Avalonia.Threading;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Views;

public partial class SessionsView : UserControl
{
    private SessionViewModel? _viewModel;

    public SessionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel != null) _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            _viewModel = DataContext as SessionViewModel;
            if (_viewModel != null) _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        };
    }

    private void OnScrollToEndRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel?.IsAutoScrollEnabled == true && LogList.ItemCount > 0)
                LogList.ScrollIntoView(LogList.ItemCount - 1);
        });
    }
}
