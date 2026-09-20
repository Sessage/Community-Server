namespace Klassenbibliothek.Services;

/// <summary>One circuit/session-wide state shared by task buttons, bulk actions and My Day.</summary>
public sealed class DailyFocusClient(IDailyFocusService service, ITodoCurrentUserService currentUser)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _timeZone;
    private string? _scope;
    public DailyFocusState? State { get; private set; }
    public string? ErrorKey { get; private set; }
    public string? FeedbackKey { get; private set; }
    public int AffectedCount { get; private set; }
    public bool IsBusy { get; private set; }
    public event Action? Changed;
    private static string Scope(TodoCurrentUser user) => user.ScopeKey ?? user.UserId;

    public void InvalidateSession()
    {
        State = null; _scope = null; FeedbackKey = null; ErrorKey = null; Changed?.Invoke();
    }

    public Task InitializeAsync(string timeZone, CancellationToken ct = default)
    {
        _timeZone = timeZone;
        return RefreshAsync(ct: ct);
    }

    public async Task RefreshAsync(bool expired = false, CancellationToken ct = default)
    {
        if (expired) { State = null; FeedbackKey = null; Changed?.Invoke(); }
        if (_timeZone is null) { ErrorKey = "Focus_Load"; Changed?.Invoke(); return; }
        await _gate.WaitAsync(ct);
        try
        {
            var user = await currentUser.GetCurrentUserAsync(ct);
            if (!user.IsAuthenticated) { State = null; _scope = null; ErrorKey = null; return; }
            if (_scope != Scope(user)) { State = null; FeedbackKey = null; }
            var state = await service.GetAsync(user.UserId, _timeZone, ct);
            var verified = await currentUser.GetCurrentUserAsync(ct);
            if (!verified.IsAuthenticated || Scope(verified) != Scope(user))
            {
                State = null; _scope = null; ErrorKey = "Focus_SessionChanged"; return;
            }
            _scope = Scope(user);
            if (State?.Date != state.Date) FeedbackKey = null;
            State = state;
            ErrorKey = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { State = null; ErrorKey = "Focus_Error"; }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async Task<bool> SetAsync(IReadOnlyCollection<Guid> ids, bool selected, CancellationToken ct = default)
    {
        // Capture the user's displayed day, before waiting for another request.
        var date = State?.Date;
        if (date is null) { ErrorKey = "Focus_Error"; Changed?.Invoke(); return false; }
        await _gate.WaitAsync(ct);
        try
        {
            // State notifications can execute consumer code. Keep them inside the protected
            // region so an exception can never leave the semaphore permanently acquired.
            IsBusy = true;
            FeedbackKey = null;
            ErrorKey = null;
            Changed?.Invoke();
            var user = await currentUser.GetCurrentUserAsync(ct);
            if (!user.IsAuthenticated || Scope(user) != _scope)
            {
                State = null; ErrorKey = "Focus_SessionChanged"; return false;
            }
            var result = await service.SetManyAsync(user.UserId, new(date.Value, selected, ids.ToArray()), ct);
            var verified = await currentUser.GetCurrentUserAsync(ct);
            if (!verified.IsAuthenticated || Scope(verified) != _scope)
            {
                State = null; ErrorKey = "Focus_SessionChanged"; return false;
            }
            State = result;
            ErrorKey = result.Date == date ? null : "Focus_DayChanged";
            FeedbackKey = ErrorKey is null ? (selected ? "Focus_AddedCount" : "Focus_RemovedCount") : null;
            AffectedCount = ids.Distinct().Count();
            if (ErrorKey is null && AffectedCount == 1) FeedbackKey = selected ? "Focus_AddedOne" : "Focus_RemovedOne";
            return ErrorKey is null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { ErrorKey = "Focus_Error"; return false; }
        finally { IsBusy = false; _gate.Release(); Changed?.Invoke(); }
    }
}
