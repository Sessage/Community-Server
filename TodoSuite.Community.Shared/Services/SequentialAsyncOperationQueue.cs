namespace Klassenbibliothek.Services;

/// <summary>
/// Executes asynchronous operations in their enqueue order without blocking the caller.
/// </summary>
internal sealed class SequentialAsyncOperationQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    public Task Enqueue(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_sync)
        {
            _tail = RunAfterAsync(_tail, operation);
            return _tail;
        }
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> operation)
    {
        // A failed operation must not permanently block newer queued work. The caller
        // remains responsible for observing and handling the returned operation task.
        try { await previous; } catch { }
        await operation();
    }
}
