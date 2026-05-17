using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LogPro.ViewModels;

namespace LogPro.Views;

public partial class SessionView : UserControl
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LogPro", "debug.log");

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
                Log($"DataContextChanged error: {ex}");
            }
        };
    }

    private static void Log(string message, Exception? ex = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] SessionView: {message}\n";
            if (ex != null)
            {
                logLine += $"  EXCEPTION: {ex.GetType().Name}\n  MESSAGE: {ex.Message}\n  STACK: {ex.StackTrace}\n";
            }
            File.AppendAllText(LogPath, logLine);
        }
        catch (Exception logEx) { System.Diagnostics.Debug.WriteLine($"[SessionView] Log failed: {logEx.Message}"); }
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
                Log("OnScrollToEndRequested error", ex);
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
            Log("OpenDirectory_Click error", ex);
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
            Log("DeleteSession_Click error", ex);
        }
    }
}
