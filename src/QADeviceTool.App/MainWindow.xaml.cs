using System.Windows;
using System.Windows.Input;
using LogPro.ViewModels;
using LogPro.Views;

namespace LogPro;

public partial class MainWindow : Window
{
    private CommandPaletteWindow? _commandPalette;

    public MainWindow()
    {
        InitializeComponent();
            PreviewKeyDown += OnPreviewKeyDown;
            }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        
        _commandPalette.AddCommand("nav:dashboard", "Go to Dashboard", "Navigate to dashboard view", "�", "Ctrl+1");
        _commandPalette.AddCommand("nav:devices", "Go to Devices", "Navigate to devices view", "??", "Ctrl+2");
        _commandPalette.AddCommand("nav:sessions", "Go to Sessions", "Navigate to sessions view", "??", "Ctrl+3");
        _commandPalette.AddCommand("nav:apps", "Go to Apps", "Navigate to app management", "??", "Ctrl+4");
        _commandPalette.AddCommand("nav:files", "Go to Files", "Navigate to file explorer", "??", "Ctrl+5");
        _commandPalette.AddCommand("nav:shell", "Go to Shell", "Navigate to ADB shell", "?", "Ctrl+6");
        _commandPalette.AddCommand("nav:vitals", "Go to Vitals", "Navigate to device vitals", "??", "Ctrl+7");
        _commandPalette.AddCommand("nav:settings", "Go to Settings", "Navigate to settings", "?", "Ctrl+,");

        _commandPalette.AddCommand("action:newSession", "Start New Session", "Start capturing logs for selected device", "?");
        _commandPalette.AddCommand("action:screenshot", "Take Screenshot", "Capture screenshot from device", "??");
        _commandPalette.AddCommand("action:mirror", "Start Mirror", "Start screen mirroring", "??");
        _commandPalette.AddCommand("action:refresh", "Refresh Devices", "Refresh connected device list", "??");
        
        _commandPalette.AddCommand("export:csv", "Export to CSV", "Export current session to CSV", "??");
        _commandPalette.AddCommand("export:json", "Export to JSON", "Export current session to JSON", "??");

        // Add feature-specific commands based on FeatureFlags
        if (FeatureFlags.AiLogAnalysis)
        {
            _commandPalette.AddCommand("ai:analyze", "AI Log Analysis", "Analyze logs for anomalies", "??");
        }

        if (FeatureFlags.MultiSelect)
        {
            _commandPalette.AddCommand("action:selectAll", "Select All Devices", "Select all connected devices", "?");
        }

        _commandPalette.CommandExecuted += OnCommandExecuted;
        _commandPalette.WindowClosed += () => _commandPalette = null;
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
        if (DataContext is MainViewModel vm)
            vm.Cleanup();
        Close();
    }
}

