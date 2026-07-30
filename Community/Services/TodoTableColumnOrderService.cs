using Klassenbibliothek.Data;
using Klassenbibliothek.Hubs;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace TodoSuite.Server.Services;

public sealed class TodoTableColumnOrderService : TodoWorkspaceServiceBase, ITodoTableColumnOrderService
{
    public TodoTableColumnOrderService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
    }

    public async Task<IReadOnlyList<string>> SetTableColumnOrderAsync(
        string userId,
        Guid listId,
        IReadOnlyList<string> orderedColumnKeys,
        CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.CustomFields)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            return [];

        if (!CanRead(userId, list))
            throw new UnauthorizedAccessException($"Tabellenspalten koennen nicht geaendert werden (Liste='{list.Name}', User='{userId}').");

        var orderedValidKeys = BuiltInTableColumns()
            .Concat((list.CustomFields ?? new())
                .OrderBy(f => f.SortOrder)
                .Select(f => $"custom:{f.Id:N}"))
            .ToList();
        var validKeys = orderedValidKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalized = (orderedColumnKeys ?? [])
            .Select(k => (k ?? "").Trim())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Where(validKeys.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalized.AddRange(orderedValidKeys.Where(k => !normalized.Contains(k, StringComparer.OrdinalIgnoreCase)));

        var pref = await GetOrCreatePreferenceAsync(db, userId, listId, cancellationToken);
        ApplyTableColumnOrder(pref, normalized);

        await SavePreferenceChangesAsync(db, userId, listId, p => ApplyTableColumnOrder(p, normalized), cancellationToken);

        return normalized;
    }

    public async Task<IReadOnlyList<string>> SetTableHiddenColumnsAsync(
        string userId,
        Guid listId,
        IReadOnlyList<string> hiddenColumnKeys,
        CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.CustomFields)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            return [];

        if (!CanRead(userId, list))
            throw new UnauthorizedAccessException($"Tabellenspalten koennen nicht ausgeblendet werden (Liste='{list.Name}', User='{userId}').");

        var validKeys = HideableTableColumns()
            .Concat((list.CustomFields ?? new()).Select(f => $"custom:{f.Id:N}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalized = (hiddenColumnKeys ?? [])
            .Select(k => (k ?? "").Trim())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Where(validKeys.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pref = await GetOrCreatePreferenceAsync(db, userId, listId, cancellationToken);
        ApplyTableHiddenColumns(pref, normalized);

        await SavePreferenceChangesAsync(db, userId, listId, p => ApplyTableHiddenColumns(p, normalized), cancellationToken);

        return normalized;
    }

    private static IReadOnlyList<string> BuiltInTableColumns()
        =>
        [
            "#",
            "open",
            "attachments",
            "comments",
            "notify",
            "title",
            "done",
            "column",
            "labels",
            "assignee",
            "start",
            "due",
            "important",
            "created"
        ];

    private static IReadOnlyList<string> HideableTableColumns()
        =>
        [
            "title",
            "done",
            "column",
            "labels",
            "assignee",
            "start",
            "due",
            "important",
            "created"
        ];

    private static async Task<ListViewPreferenceEntity> GetOrCreatePreferenceAsync(
        ApplicationDbContext db,
        string userId,
        Guid listId,
        CancellationToken cancellationToken)
    {
        var pref = await db.ListViewPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ListId == listId, cancellationToken);

        if (pref is not null)
            return pref;

        pref = new ListViewPreferenceEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ListId = listId,
            UpdatedAtUtc = DateTime.UtcNow
        };
        db.ListViewPreferences.Add(pref);
        return pref;
    }

    private static void ApplyTableColumnOrder(ListViewPreferenceEntity pref, IReadOnlyList<string> normalized)
    {
        pref.TableColumnOrder = normalized.ToList();
        pref.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void ApplyTableHiddenColumns(ListViewPreferenceEntity pref, IReadOnlyList<string> normalized)
    {
        pref.TableHiddenColumns = normalized.ToList();
        pref.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task SavePreferenceChangesAsync(
        ApplicationDbContext db,
        string userId,
        Guid listId,
        Action<ListViewPreferenceEntity> apply,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            foreach (var entry in db.ChangeTracker.Entries<ListViewPreferenceEntity>().Where(e => e.State == EntityState.Added))
                entry.State = EntityState.Detached;

            var existing = await db.ListViewPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId && p.ListId == listId, cancellationToken);

            if (existing is null)
                throw;

            apply(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException) as string == "23505";
}
