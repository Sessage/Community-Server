namespace Klassenbibliothek.Services;

public sealed record DailyFocusState(DateOnly Date, string TimeZoneId, DateTimeOffset ResetsAtUtc, IReadOnlyList<Guid> Ids);
public sealed record DailyFocusChange(DateOnly Date, bool Selected);
public sealed record DailyFocusBatchChange(DateOnly Date, bool Selected, IReadOnlyList<Guid> TaskIds);

public interface IDailyFocusService
{
    Task<DailyFocusState> GetAsync(string userId, string timeZoneId, CancellationToken ct = default);
    Task<DailyFocusState> SetAsync(string userId, Guid taskId, DailyFocusChange change, CancellationToken ct = default);
    Task<DailyFocusState> SetManyAsync(string userId, DailyFocusBatchChange change, CancellationToken ct = default);
}
