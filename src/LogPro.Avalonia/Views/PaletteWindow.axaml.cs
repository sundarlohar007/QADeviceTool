using Avalonia.Controls;
using Avalonia.Input;
using LogPro.ViewModels;

namespace LogPro.Avalonia.Views;

public sealed record PaletteItem(string Title, string Hint, Action Run);

/// <summary>Ctrl/Cmd+K command palette (§11.5) — every nav destination + core actions.</summary>
public partial class PaletteWindow : Window
{
    private readonly List<PaletteItem> _all = new();

    public PaletteWindow()
    {
        InitializeComponent();
        Opened += (_, _) => SearchBox.Focus();
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        KeyDown += OnKeyDown;
        ApplyFilter();
    }

    public static PaletteWindow Create(MainViewModel vm)
    {
        var window = new PaletteWindow();
        window._all.Add(new PaletteItem("Dashboard", "nav", () => vm.NavigateCommand.Execute("dashboard")));
        window._all.Add(new PaletteItem("Sessions", "nav", () => vm.NavigateCommand.Execute("sessions")));
        window._all.Add(new PaletteItem("Devices", "nav", () => vm.NavigateCommand.Execute("device")));
        window._all.Add(new PaletteItem("Apps", "nav", () => vm.NavigateCommand.Execute("apps")));
        window._all.Add(new PaletteItem("Files", "nav", () => vm.NavigateCommand.Execute("files")));
        window._all.Add(new PaletteItem("Shell", "nav", () => vm.NavigateCommand.Execute("shell")));
        window._all.Add(new PaletteItem("Deep Link", "nav", () => vm.NavigateCommand.Execute("deeplink")));
        window._all.Add(new PaletteItem("Vitals", "nav", () => vm.NavigateCommand.Execute("vitals")));
        window._all.Add(new PaletteItem("Macros", "nav", () => vm.NavigateCommand.Execute("macros")));
        window._all.Add(new PaletteItem("Stress Test", "nav", () => vm.NavigateCommand.Execute("stresstest")));
        window._all.Add(new PaletteItem("Performance", "nav", () => vm.NavigateCommand.Execute("performance")));
        window._all.Add(new PaletteItem("Settings", "nav", () => vm.NavigateCommand.Execute("settings")));

        window._all.Add(new PaletteItem("Refresh Devices", "action", () => vm.DeviceVM.RefreshDevicesCommand.Execute(null)));
        window._all.Add(new PaletteItem("Start Capture", "action", () => vm.SessionVM.StartCaptureCommand.Execute(null)));
        window._all.Add(new PaletteItem("Start Session", "action", () => vm.DashboardVM.QuickStartSessionCommand.Execute(null)));
        window._all.Add(new PaletteItem("Take Snapshot", "action", () => vm.DeviceVM.TakeSnapshotCommand.Execute(null)));
        window._all.Add(new PaletteItem("Start Profiling", "action", () => vm.ProfilerVM.StartProfilingCommand.Execute(null)));
        window._all.Add(new PaletteItem("Stop Profiling", "action", () => vm.ProfilerVM.StopProfilingCommand.Execute(null)));

        window.ApplyFilter();
        return window;
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        var matches = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(i =>
                  i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                  i.Hint.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ResultsList.ItemsSource = matches;
        if (matches.Count > 0) ResultsList.SelectedIndex = 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Enter:
                if (ResultsList.SelectedItem is PaletteItem item)
                {
                    Close();
                    item.Run();
                }
                e.Handled = true;
                break;

            case Key.Down when ResultsList.Items.Count > 0:
                ResultsList.SelectedIndex = (ResultsList.SelectedIndex + 1) % ResultsList.Items.Count;
                e.Handled = true;
                break;

            case Key.Up when ResultsList.Items.Count > 0:
                ResultsList.SelectedIndex = (ResultsList.SelectedIndex - 1 + ResultsList.Items.Count) % ResultsList.Items.Count;
                e.Handled = true;
                break;
        }
    }
}
