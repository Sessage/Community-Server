namespace TodoSuite.Server.Services;

/// <summary>
/// Informiert Komponenten innerhalb desselben Blazor-Circuits sofort über eine
/// serverseitig abgeschlossene Navigationsänderung. SignalR bleibt für andere
/// Browser und mobile Clients zuständig.
/// </summary>
public sealed class NavigationRefreshNotifier
{
    public event Func<Task>? RefreshRequested;

    public async Task RequestRefreshAsync()
    {
        var handlers = RefreshRequested;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
            await handler();
    }
}
