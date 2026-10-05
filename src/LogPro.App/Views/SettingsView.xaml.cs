using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LogPro.Services;
using LogPro.ViewModels;

namespace LogPro.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _settings;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_settings != null) _settings.PropertyChanged -= Settings_PropertyChanged;
            _settings = DataContext as SettingsViewModel;
            if (_settings != null) _settings.PropertyChanged += Settings_PropertyChanged;
            PairingCodeInput.Clear();
        };
    }

    private void PairingCodeInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.PairingCode = PairingCodeInput.Password;
    }

    private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.PairingCode) &&
            _settings?.PairingCode.Length == 0 && PairingCodeInput.Password.Length != 0)
            PairingCodeInput.Clear();
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
