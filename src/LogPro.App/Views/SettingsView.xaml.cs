using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LogPro.Services;

namespace LogPro.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        // External browser launches are disabled by the offline hard gate.
        AppLogger.Log.Info("[Settings] External link launch blocked by offline security policy");
        MessageBox.Show("External links are disabled while offline security mode is active.",
            "Offline Security", MessageBoxButton.OK, MessageBoxImage.Information);
        e.Handled = true;
    }
}
