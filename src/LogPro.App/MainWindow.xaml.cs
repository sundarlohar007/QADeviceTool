using System.Windows;
using System.Windows.Input;
using LogPro.ViewModels;
using LogPro.Views;

namespace LogPro;

public partial class MainWindow : Window
{
    private CommandPaletteWindow? _commandPalette;

    public bool IsThemeSwitching { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => Services.WindowPlacementService.Restore(this);
        Closing += (_, _) =>
        {
            Services.WindowPlacementService.Save(this);
            _commandPalette?.Close();
            if (!IsThemeSwitching && DataContext is MainViewModel vm) vm.Cleanup();
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old)
            {
                old.SettingsVM.ElevatedUpdateRequested -= OnElevatedUpdateRequested;
                old.SettingsVM.RestartForUpdateRequested -= CloseForUpdate;
            }
            if (e.NewValue is MainViewModel current)
            {
                current.SettingsVM.ElevatedUpdateRequested += OnElevatedUpdateRequested;
                current.SettingsVM.RestartForUpdateRequested += CloseForUpdate;
            }
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+1..7: Navigate views
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D7)
        {
            var navMap = new[] { "dashboard", "devices", "sessions", "apps", "files", "shell", "vitals" };
            var idx = e.Key - Key.D1;
            if (idx >= 0 && idx < navMap.Length && DataContext is MainViewModel vm)
            {
                vm.NavigateCommand.Execute(navMap[idx]);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control && DataContext is MainViewModel settingsVm)
        {
            settingsVm.NavigateCommand.Execute("settings");
            e.Handled = true;
        }
        // Ctrl+K: Command palette
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowCommandPalette();
            e.Handled = true;
        }
    }

    private void ShowCommandPalette()
    {
        if (_commandPalette != null) return;

        _commandPalette = new CommandPaletteWindow();

        _commandPalette.AddCommand("nav:dashboard", "Go to Dashboard", "Navigate to dashboard view", "", "Ctrl+1");
        _commandPalette.AddCommand("nav:devices", "Go to Devices", "Navigate to devices view", "", "Ctrl+2");
        _commandPalette.AddCommand("nav:sessions", "Go to Sessions", "Navigate to sessions view", "", "Ctrl+3");
        _commandPalette.AddCommand("nav:apps", "Go to Apps", "Navigate to app management", "", "Ctrl+4");
        _commandPalette.AddCommand("nav:files", "Go to Files", "Navigate to file explorer", "", "Ctrl+5");
        _commandPalette.AddCommand("nav:shell", "Go to Shell", "Navigate to ADB shell", "", "Ctrl+6");
        _commandPalette.AddCommand("nav:vitals", "Go to Vitals", "Navigate to device vitals", "", "Ctrl+7");
        _commandPalette.AddCommand("nav:settings", "Go to Settings", "Navigate to settings", "", "Ctrl+,");

        _commandPalette.AddCommand("action:newSession", "Start New Session", "Start capturing logs for selected device", "");
        _commandPalette.AddCommand("action:screenshot", "Take Screenshot", "Capture screenshot from device", "");
        _commandPalette.AddCommand("action:mirror", "Start Mirror", "Start screen mirroring", "");
        _commandPalette.AddCommand("action:refresh", "Refresh Devices", "Refresh connected device list", "");

        _commandPalette.AddCommand("export:csv", "Export to CSV", "Export current session to CSV", "");
        _commandPalette.AddCommand("export:json", "Export to JSON", "Export current session to JSON", "");

        _commandPalette.CommandExecuted += OnCommandExecuted;
        _commandPalette.WindowClosed += () => _commandPalette = null;
        _commandPalette.Owner = this;
        _commandPalette.Show();
    }

    private void OnCommandExecuted(string commandId)
    {
        if (DataContext is not MainViewModel vm) return;

        if (commandId.StartsWith("nav:"))
        {
            var viewName = commandId.Replace("nav:", "");
            vm.NavigateCommand.Execute(viewName);
        }
        else if (commandId == "action:refresh")
        {
            vm.DeviceVM.RefreshDevicesCommand.Execute(null);
        }
        else
        {
            ICommand? command = commandId switch
            {
                "action:newSession" => vm.DashboardVM.QuickStartSessionCommand,
                "action:screenshot" => vm.DashboardVM.QuickSnapshotCommand,
                "action:mirror" => vm.DashboardVM.QuickMirrorCommand,
                "export:csv" => vm.SessionVM.ExportCsvCommand,
                "export:json" => vm.SessionVM.ExportJsonCommand,
                _ => null
            };
            if (command?.CanExecute(null) == true) command.Execute(null);
        }
    }

    private void CloseForUpdate() => Dispatcher.Invoke(Close);

    private void OnElevatedUpdateRequested(string operation)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--update-tool {LogPro.Helpers.ToolLauncher.QuoteArgument(operation)} --wait-process {Environment.ProcessId}"
            });
            Close();
        }
        catch (Exception ex)
        {
            if (DataContext is MainViewModel vm) vm.SettingsVM.UpdateStatus =
                "Windows updater was not started: " + LogPro.Helpers.SecurityHelper.RedactSensitiveText(ex.Message);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!IsThemeSwitching && DataContext is MainViewModel vm)
            vm.Cleanup();
        Close();
    }
}

