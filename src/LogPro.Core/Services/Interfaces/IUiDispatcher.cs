namespace LogPro.Services;

/// <summary>
/// Platform-agnostic marshalling onto the UI thread. Keeps ViewModels (and the
/// future engine) free of any direct WPF/Dispatcher dependency.
/// </summary>
public interface IUiDispatcher
{
    bool IsOnUiThread { get; }
    void Post(Action action);
    Task InvokeAsync(Action action);
}