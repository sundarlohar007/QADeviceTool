using CommunityToolkit.Mvvm.ComponentModel;
using LogPro.Models;

namespace LogPro.ViewModels;

public partial class LogLevelFilterItem : ObservableObject
{
    [ObservableProperty]
    private LogLevel _level;

    [ObservableProperty]
    private bool _isSelected = true;

    public string DisplayName => Level.ToString();

    public LogLevelFilterItem() { }

    public LogLevelFilterItem(LogLevel level, bool isSelected = true)
    {
        _level = level;
        _isSelected = isSelected;
    }
}
