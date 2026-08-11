using System.Windows.Threading;

namespace LogPro.Services;

/// <summary>
/// WPF implementation of <see cref="IUiDispatcher"/>. Deliberately thin:
/// <see cref="Post"/> maps to BeginInvoke, <see cref="InvokeAsync"/> marshals and awaits.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public bool IsOnUiThread => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        _dispatcher.BeginInvoke(action);
    }

    public async Task InvokeAsync(Action action)
    {
        // WPF semantics: inline when already marshalling from the UI thread.
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }
        await _dispatcher.InvokeAsync(action);
    }
}