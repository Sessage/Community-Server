using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Microsoft.EntityFrameworkCore;

namespace TodoSuite.Server.Services;

public sealed class DailyFocusService(IDbContextFactory<ApplicationDbContext> factory, TimeProvider? clock = null) : IDailyFocusService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<DailyFocusState> GetAsync(string userId, string timeZoneId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await using var db = await factory.CreateDbContextAsync(ct);
        var preference = await db.DailyFocusPreferences.FindAsync([userId], ct);
        if (preference is null)
        {
            // Only the first device chooses the account calendar. Later devices cannot
            // reset the plan by sending their own timezone (e.g. while travelling).
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            preference = new() { UserId = userId, TimeZoneId = timeZoneId };
            db.DailyFocusPreferences.Add(preference);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                preference = await db.DailyFocusPreferences.FindAsync([userId], ct);
                if (preference is null) throw;
            }
        }
        return await ReadAsync(db, preference, ct);
    }

    public Task<DailyFocusState> SetAsync(string userId, Guid taskId, DailyFocusChange change, CancellationToken ct = default)
        => SetManyAsync(userId, new(change.Date, change.Selected, [taskId]), ct);

    public async Task<DailyFocusState> SetManyAsync(string userId, DailyFocusBatchChange change, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (change.TaskIds is null || change.TaskIds.Count is < 1 or > 500 || change.TaskIds.Contains(Guid.Empty))
            throw new ArgumentException("Select between 1 and 500 valid tasks.", nameof(change));
        var ids = change.TaskIds.Distinct().ToArray();
        // SaveChanges commits the whole batch in one transaction. On a duplicate add
        // or concurrent delete, reload and retry the idempotent intent, never a toggle.
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var preference = await db.DailyFocusPreferences.FindAsync([userId], ct)
                ?? throw new InvalidOperationException("Load My Day before changing its selection.");
            var state = await ReadAsync(db, preference, ct);
            if (change.Date != state.Date) return state;
            if (await AccessibleTasks(db, userId).CountAsync(t => ids.Contains(t.Id), ct) != ids.Length)
                throw new UnauthorizedAccessException("One or more tasks are not accessible.");

            var rows = await db.DailyFocusSelections
                .Where(x => x.UserId == userId && x.Date == change.Date && ids.Contains(x.TaskId)).ToListAsync(ct);
            if (change.Selected)
            {
                var existing = rows.Select(x => x.TaskId).ToHashSet();
                db.DailyFocusSelections.AddRange(ids.Where(id => !existing.Contains(id))
                    .Select(id => new DailyFocusSelectionEntity { UserId = userId, Date = change.Date, TaskId = id }));
            }
            else db.DailyFocusSelections.RemoveRange(rows);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) when (attempt < 2) { continue; }
            catch (DbUpdateException ex) when (attempt < 2 && ex.InnerException is Npgsql.PostgresException
                { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_DailyFocusSelections" }) { continue; }
            return await ReadAsync(db, preference, ct);
        }
    }
    private static IQueryable<TodoTaskEntity> AccessibleTasks(ApplicationDbContext db, string userId) =>
        db.TodoTasks.Where(t => t.DeletedAt == null && t.List != null && t.List.DeletedAt == null && !t.List.IsTemplate
            && (t.List.OwnerId == userId || t.List.Participants.Any(p => !p.InvitationPending && (p.UserId == userId || p.Email == userId))));

    private async Task<DailyFocusState> ReadAsync(ApplicationDbContext db, DailyFocusPreferenceEntity preference, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(preference.TimeZoneId);
        var now = _clock.GetUtcNow();
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var midnight = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        // Some zones advance their clocks at midnight.
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
        var reset = zone.IsAmbiguousTime(midnight)
            ? new DateTimeOffset(midnight, zone.GetAmbiguousTimeOffsets(midnight).Max()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone));
        var tasks = AccessibleTasks(db, preference.UserId).Select(t => t.Id);
        var ids = await db.DailyFocusSelections.AsNoTracking()
            .Where(x => x.UserId == preference.UserId && x.Date == date && tasks.Contains(x.TaskId))
            .OrderBy(x => x.TaskId).Select(x => x.TaskId).ToListAsync(ct);
        return new(date, preference.TimeZoneId, reset, ids);
    }
}
