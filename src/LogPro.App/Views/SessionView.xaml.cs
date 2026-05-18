using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LogPro.ViewModels;
using LogPro.Services;

namespace LogPro.Views;

public partial class SessionView : UserControl
{
    private SessionViewModel? _vm;

    public SessionView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            try
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
            }
            catch (Exception ex)
            {
                AppLogger.Log.Debug(ex, "SessionView DataContextChanged error");
            }
        };
    }


    private void OnScrollToEndRequested()
    {
        if (_vm == null || !_vm.IsAutoScrollEnabled) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                if (LogList.Items.Count > 0)
                {
                    var lastItem = LogList.Items[LogList.Items.Count - 1];
                    LogList.ScrollIntoView(lastItem);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log.Debug("OnScrollToEndRequested error", ex);
            }
        });
    }

    /// <summary>
    /// Selects the session under the mouse cursor before the context menu opens.
    /// Uses VisualTreeHelper.HitTest to find which ListBoxItem was right-clicked.
    /// </summary>
    private void SessionList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;

        var listBox = sender as ListBox;
        if (listBox == null) return;

        // Hit test to find which element is under the mouse
        var hitElement = listBox.InputHitTest(e.GetPosition(listBox)) as DependencyObject;
        if (hitElement == null) return;

        // Walk up visual tree to find the ListBoxItem
        while (hitElement != null && hitElement != listBox)
        {
            if (hitElement is ListBoxItem listBoxItem)
            {
                listBoxItem.IsSelected = true;
                return;
            }
            hitElement = VisualTreeHelper.GetParent(hitElement);
        }
    }

    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm?.SelectedSession == null) return;
            _vm.OpenSessionFolderCommand.Execute(null);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Debug("OpenDirectory_Click error", ex);
        }
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm?.SelectedSession == null) return;
            _vm.DeleteSessionCommand.Execute(null);
        }
        catch (Exception ex)
        {
            AppLogger.Log.Debug("DeleteSession_Click error", ex);
        }
    }
}
