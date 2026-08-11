using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LogPro.Views;

public partial class CommandPaletteWindow : Window, INotifyPropertyChanged
{
    private string _searchText = string.Empty;
    private CommandItem? _selectedCommand;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? CommandExecuted;
    public event Action? WindowClosed;

    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            OnPropertyChanged();
            FilterCommands();
        }
    }

    public CommandItem? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            _selectedCommand = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<CommandItem> AllCommands { get; } = new();
    public ObservableCollection<CommandItem> FilteredCommands { get; } = new();

    public CommandPaletteWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        Deactivated += OnDeactivated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();

        var fadeIn = (Storyboard)Resources["FadeIn"];
        BeginStoryboard(fadeIn);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseWindow();
                break;
            case Key.Enter:
                ExecuteSelectedCommand();
                break;
            case Key.Up:
                NavigateUp();
                e.Handled = true;
                break;
            case Key.Down:
                NavigateDown();
                e.Handled = true;
                break;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        CloseWindow();
    }

    private void CloseWindow()
    {
        var fadeOut = (Storyboard)Resources["FadeOut"];
        EventHandler handler = null!;
        handler = (s, e) =>
        {
            fadeOut.Completed -= handler;
            WindowClosed?.Invoke();
            Close();
        };
        fadeOut.Completed += handler;
        BeginStoryboard(fadeOut);
    }

    private void NavigateUp()
    {
        if (FilteredCommands.Count == 0) return;
        var index = FilteredCommands.IndexOf(SelectedCommand!);
        if (index > 0)
            SelectedCommand = FilteredCommands[index - 1];
        else
            SelectedCommand = FilteredCommands[^1];
    }

    private void NavigateDown()
    {
        if (FilteredCommands.Count == 0) return;
        var index = FilteredCommands.IndexOf(SelectedCommand!);
        if (index < FilteredCommands.Count - 1)
            SelectedCommand = FilteredCommands[index + 1];
        else
            SelectedCommand = FilteredCommands[0];
    }

    private void ExecuteSelectedCommand()
    {
        if (SelectedCommand != null)
        {
            CommandExecuted?.Invoke(SelectedCommand.Id);
            Close();
        }
    }

    private void FilterCommands()
    {
        FilteredCommands.Clear();
        var search = SearchText?.ToLowerInvariant() ?? "";

        foreach (var cmd in AllCommands)
        {
            if (string.IsNullOrEmpty(search) ||
                cmd.Title.ToLowerInvariant().Contains(search) ||
                cmd.Description.ToLowerInvariant().Contains(search))
            {
                FilteredCommands.Add(cmd);
            }
        }

        if (FilteredCommands.Count > 0)
            SelectedCommand = FilteredCommands[0];
    }

    public void AddCommand(string id, string title, string description, string icon, string shortcut = "")
    {
        AllCommands.Add(new CommandItem { Id = id, Title = title, Description = description, Icon = icon, Shortcut = shortcut });
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class CommandItem : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Shortcut { get; set; } = "";

#pragma warning disable CS0067 // Event is never used
    public event PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067
}
