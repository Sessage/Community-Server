using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Services;

public class TaskMemberService : ITaskMemberService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public TaskMemberService(IDbContextFactory<ApplicationDbContext> dbFactory)
        => _dbFactory = dbFactory;

    private static bool LooksLikeEmail(string? s)
        => !string.IsNullOrWhiteSpace(s) && s.Contains('@');

    public async Task<IReadOnlyList<ListParticipantEntity>> GetEligibleMembersAsync(
        string callerUserId,
        Guid listId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId, ct);

        if (list is null) return Array.Empty<ListParticipantEntity>();

        // Optional: Rechte prüfen (analog zu CanWrite)
        // -> hier weggelassen, weil du es im WorkspaceService zentral machst

        return (list.Participants ?? new())
            .Where(p => !p.InvitationPending)
            .Where(p => !string.IsNullOrWhiteSpace(p.UserId))
            .Where(p => !LooksLikeEmail(p.UserId))
            .GroupBy(p => p.UserId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.DisplayName ?? p.Email ?? p.UserId)
            .ToList();
    }

    public async Task SetTaskMembersAsync(
    string callerUserId,
    Guid listId,
    Guid taskId,
    IReadOnlyCollection<string> memberUserIds,
    CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId, ct);

        if (list is null)
            throw new InvalidOperationException(
                $"Mitglieder konnten nicht gesetzt werden: Liste nicht gefunden. ListId='{listId}', CallerUserId='{callerUserId}'.");

        var taskExists = await db.TodoTasks
            .AnyAsync(t => t.Id == taskId && t.ListId == listId, ct);

        if (!taskExists)
            throw new InvalidOperationException(
                $"Mitglieder konnten nicht gesetzt werden: Aufgabe nicht gefunden. TaskId='{taskId}', ListId='{listId}', CallerUserId='{callerUserId}'.");

        // Whitelist: nur gültige Teilnehmer-UserIds
        var eligible = (list.Participants ?? new())
            .Where(p => !p.InvitationPending)
            .Where(p => !string.IsNullOrWhiteSpace(p.UserId))
            .Select(p => p.UserId!.Trim())
            .Where(u => !LooksLikeEmail(u))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var desired = (memberUserIds ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !LooksLikeEmail(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var invalid = desired.Where(x => !eligible.Contains(x)).ToList();
        if (invalid.Count > 0)
            throw new ArgumentException(
                $"Mitglieder konnten nicht gesetzt werden: Nicht berechtigte UserIds: {string.Join(", ", invalid)}. TaskId='{taskId}', ListId='{listId}', CallerUserId='{callerUserId}'.",
                nameof(memberUserIds));

        // ✅ WICHTIG: bestehende Members direkt aus Tabelle laden (nicht über Task-Graph)
        var existing = await db.TodoTaskMembers
            .Where(m => m.TaskId == taskId)
            .ToListAsync(ct);

        // Entfernen
        var toRemove = existing
            .Where(m => !desired.Contains(m.UserId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (toRemove.Count > 0)
            db.TodoTaskMembers.RemoveRange(toRemove);

        // Hinzufügen
        var existingUserIds = existing
            .Select(m => m.UserId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toAdd = desired
            .Where(u => !existingUserIds.Contains(u))
            .Select(u => new TodoTaskMemberEntity
            {
                // Falls deine Entity eine Guid-PK "Id" hat, setz sie explizit:
                Id = Guid.NewGuid(),
                TaskId = taskId,
                UserId = u
            })
            .ToList();

        if (toAdd.Count > 0)
            await db.TodoTaskMembers.AddRangeAsync(toAdd, ct);

        await db.SaveChangesAsync(ct);
    }


    public async Task CleanupRemovedListMembersAsync(
        Guid listId,
        IReadOnlyCollection<string> removedUserIds,
        CancellationToken ct = default)
    {
        var removed = (removedUserIds ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (removed.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1) Assignee bereinigen
        var tasksWithAssignee = await db.TodoTasks
            .Where(t => t.ListId == listId && t.Assignee != null)
            .ToListAsync(ct);

        foreach (var t in tasksWithAssignee)
        {
            if (t.Assignee is not null && removed.Contains(t.Assignee.Trim(), StringComparer.OrdinalIgnoreCase))
                t.Assignee = null;
        }

        // 2) TaskMembers bereinigen
        var toDelete = await db.TodoTaskMembers
            .Where(m => m.Task!.ListId == listId && removed.Contains(m.UserId))
            .ToListAsync(ct);

        db.TodoTaskMembers.RemoveRange(toDelete);

        await db.SaveChangesAsync(ct);
    }
}
