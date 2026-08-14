using Avalonia.Threading;
using LogPro.Services;

namespace LogPro.Avalonia.Services;

/// <summary>Avalonia implementation of the engine's IUiDispatcher — the §4.1 adapter.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
