using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;

namespace TodoSuite.Server.Services;

/// <summary>
/// Verwaltet den Papierkorb: gelöschte Listen und Aufgaben, Wiederherstellung und Endgültig-Löschen.
/// </summary>
public class TodoTrashService : TodoWorkspaceServiceBase, ITodoTrashService
{
    private const int RetentionDays = 14;
    private const long CleanupAdvisoryLockKey = 6075445457406612307L;
    private readonly ILogger<TodoTrashService> _logger;

    public TodoTrashService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService,
        ILogger<TodoTrashService> logger)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoListEntity>> GetDeletedListsAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

        return await db.TodoLists
            .Include(l => l.Participants)
            .Where(l => l.DeletedAt >= cutoff &&
                        (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending
                            && p.Role == ListRole.Admin
                            && (p.Email == userId || p.UserId == userId))))
            .OrderByDescending(l => l.DeletedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeletedTaskTrashItem>> GetDeletedTaskEntriesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

        return await db.TodoTasks
            .Where(task => task.DeletedAt >= cutoff
                && task.List != null
                && task.List.DeletedAt == null
                && (task.List.OwnerId == userId
                    || task.List.Participants.Any(participant => !participant.InvitationPending
                        && participant.Role != ListRole.Observer
                        && (participant.UserId == userId || participant.Email == userId))))
            .OrderByDescending(task => task.DeletedAt)
            .Select(task => new DeletedTaskTrashItem(
                task.Id,
                task.ListId,
                task.Title,
                task.List!.Name,
                task.DeletedAt!.Value))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoTaskEntity>> GetDeletedTasksAsync(string userId, Guid listId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

        // Einzelne Aufgaben werden nur in einer aktiven Liste wiederhergestellt. Bei einer
        // gelöschten Liste muss zuerst die Liste selbst aus dem Papierkorb geholt werden.
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            return Array.Empty<TodoTaskEntity>();

        if (!CanWrite(userId, list))
            return Array.Empty<TodoTaskEntity>();

        return await db.TodoTasks
            .Where(t => t.ListId == listId && t.DeletedAt >= cutoff)
            .OrderByDescending(t => t.DeletedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RestoreListAsync(string userId, Guid listId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

        var entity = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt >= cutoff, cancellationToken);

        if (entity is null)
            return false;

        if (!CanAdmin(userId, entity))
            throw new UnauthorizedAccessException($"Liste '{entity.Name}' kann nicht wiederhergestellt werden (User='{userId}').");

        entity.DeletedAt = null;
        entity.DeletedByUserId = null;
        entity.ContentVersion++;

        await db.SaveChangesAsync(cancellationToken);
        await TryNotifyParticipantsListsUpdatedAsync(entity, cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RestoreTaskAsync(string userId, Guid listId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            return false;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Aufgabe kann nicht wiederhergestellt werden (Liste='{list.Name}', User='{userId}').");

        var task = await db.TodoTasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.ListId == listId && t.DeletedAt >= cutoff, cancellationToken);

        if (task is null)
            return false;

        task.Column = ResolveRestoreColumn(list, task);
        task.Done = (list.DoneColumns ?? []).Contains(task.Column, StringComparer.OrdinalIgnoreCase);
        task.ListSortOrder = await GetNextListSortOrderAsync(db, listId, cancellationToken);
        task.KanbanSortOrder = await GetNextKanbanSortOrderAsync(db, listId, task.Column, cancellationToken);
        task.DeletedAt = null;
        task.DeletedByUserId = null;
        task.ContentVersion++;

        await db.SaveChangesAsync(cancellationToken);
        await TryNotifyListUpdatedAsync(listId, cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var cleanupLease = await TryAcquireCleanupLeaseAsync(db, cancellationToken);
        if (cleanupLease is null)
        {
            _logger.LogDebug("Papierkorb-Bereinigung überspringt diesen Lauf, weil eine andere Instanz bereits arbeitet.");
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var purged = 0;

        // Aufgaben und Listen einschließlich zugehöriger Metadaten in einem Commit löschen.
        // Physische Dateien werden erst danach entfernt, damit ein DB-Fehler keine gültigen
        // Anhangsdatensätze ohne Datei zurücklässt.
        var expiredTasks = await db.TodoTasks
            .Where(t => t.DeletedAt != null && t.DeletedAt < cutoff)
            .ToListAsync(cancellationToken);
        var expiredLists = await db.TodoLists
            .Include(list => list.Tasks)
            .Where(l => l.DeletedAt != null && l.DeletedAt < cutoff)
            .ToListAsync(cancellationToken);
        var expiredTaskIds = expiredTasks.Select(task => task.Id).ToHashSet();
        var expiredListIds = expiredLists.Select(list => list.Id).ToHashSet();
        var attachmentIds = await db.TodoAttachments
            .Where(attachment => expiredTaskIds.Contains(attachment.TaskId)
                || db.TodoTasks.Any(task => task.Id == attachment.TaskId && expiredListIds.Contains(task.ListId)))
            .Select(attachment => attachment.Id)
            .Distinct()
            .ToListAsync(cancellationToken);
        var expiredNotifications = await db.UserNotifications
            .Where(notification => expiredListIds.Contains(notification.ListId)
                || (notification.TaskId.HasValue && expiredTaskIds.Contains(notification.TaskId.Value)))
            .ToListAsync(cancellationToken);
        var expiredDirectoryGrants = await db.DirectoryShareGrants
            .Where(grant => grant.ResourceType == DirectoryShareResourceType.List
                            && expiredListIds.Contains(grant.ResourceId))
            .ToListAsync(cancellationToken);

        db.UserNotifications.RemoveRange(expiredNotifications);
        db.DirectoryShareGrants.RemoveRange(expiredDirectoryGrants);
        db.TodoTasks.RemoveRange(expiredTasks);
        db.TodoLists.RemoveRange(expiredLists);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var attachmentId in attachmentIds)
            TryDeleteAttachmentFile(attachmentId);

        purged += expiredTasks.Count + expiredLists.Count;

        return purged;
    }

    private static string ResolveRestoreColumn(TodoListEntity list, TodoTaskEntity task)
    {
        var columns = (list.Columns ?? [])
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (columns.Count == 0)
            return "Backlog";

        var existing = columns.FirstOrDefault(column => string.Equals(column, task.Column, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var doneColumns = list.DoneColumns ?? [];
        return task.Done
            ? columns.FirstOrDefault(column => doneColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) ?? columns[0]
            : columns.FirstOrDefault(column => !doneColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) ?? columns[0];
    }

    private static async Task<int> GetNextListSortOrderAsync(ApplicationDbContext db, Guid listId, CancellationToken ct)
    {
        var max = await db.TodoTasks
            .Where(task => task.ListId == listId && task.DeletedAt == null)
            .Select(task => (int?)task.ListSortOrder)
            .MaxAsync(ct);
        if (!max.HasValue)
            return 0;
        if (max.Value < int.MaxValue)
            return max.Value + 1;

        var activeTasks = await db.TodoTasks
            .Where(task => task.ListId == listId && task.DeletedAt == null)
            .OrderBy(task => task.ListSortOrder)
            .ThenBy(task => task.CreatedAtUtc)
            .ThenBy(task => task.Id)
            .ToListAsync(ct);
        for (var index = 0; index < activeTasks.Count; index++)
            activeTasks[index].ListSortOrder = index;
        return activeTasks.Count;
    }

    private static async Task<int> GetNextKanbanSortOrderAsync(ApplicationDbContext db, Guid listId, string column, CancellationToken ct)
    {
        var max = await db.TodoTasks
            .Where(task => task.ListId == listId && task.DeletedAt == null && task.Column == column)
            .Select(task => (int?)task.KanbanSortOrder)
            .MaxAsync(ct);
        if (!max.HasValue)
            return 0;
        if (max.Value < int.MaxValue)
            return max.Value + 1;

        var columnTasks = await db.TodoTasks
            .Where(task => task.ListId == listId && task.DeletedAt == null && task.Column == column)
            .OrderBy(task => task.KanbanSortOrder)
            .ThenBy(task => task.CreatedAtUtc)
            .ThenBy(task => task.Id)
            .ToListAsync(ct);
        for (var index = 0; index < columnTasks.Count; index++)
            columnTasks[index].KanbanSortOrder = index;
        return columnTasks.Count;
    }

    private async Task TryNotifyListUpdatedAsync(Guid listId, CancellationToken ct)
    {
        try { await NotifyListUpdatedAsync(listId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Live-Aktualisierung nach Aufgaben-Wiederherstellung fehlgeschlagen. ListId={ListId}", listId); }
    }

    private async Task TryNotifyParticipantsListsUpdatedAsync(TodoListEntity list, CancellationToken ct)
    {
        try { await NotifyParticipantsListsUpdatedAsync(list, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Live-Aktualisierung nach Listen-Wiederherstellung fehlgeschlagen. ListId={ListId}", list.Id); }
    }

    private void TryDeleteAttachmentFile(Guid attachmentId)
    {
        var diskPath = Path.Combine(UploadRoot, attachmentId.ToString("N"));
        try
        {
            if (File.Exists(diskPath))
                File.Delete(diskPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verwaiste Anhangsdatei konnte nach Papierkorb-Bereinigung nicht entfernt werden. AttachmentId={AttachmentId}", attachmentId);
        }
    }

    private async Task<TrashCleanupLease?> TryAcquireCleanupLeaseAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (!string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return new TrashCleanupLease(db, ownsDatabaseConnection: false, lockHeld: false, _logger);

        var connection = db.Database.GetDbConnection();
        var ownsConnection = connection.State != ConnectionState.Open;
        if (ownsConnection)
            await db.Database.OpenConnectionAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT pg_try_advisory_lock({CleanupAdvisoryLockKey})";
            var result = await command.ExecuteScalarAsync(ct);
            var acquired = result is not null && result != DBNull.Value && Convert.ToBoolean(result);
            if (!acquired)
            {
                if (ownsConnection)
                    await db.Database.CloseConnectionAsync();
                return null;
            }

            return new TrashCleanupLease(db, ownsConnection, lockHeld: true, _logger);
        }
        catch
        {
            if (ownsConnection)
                await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    private sealed class TrashCleanupLease(
        ApplicationDbContext db,
        bool ownsDatabaseConnection,
        bool lockHeld,
        ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (lockHeld)
            {
                try
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = $"SELECT pg_advisory_unlock({CleanupAdvisoryLockKey})";
                    await command.ExecuteScalarAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Der Datenbank-Lock der Papierkorb-Bereinigung konnte nicht explizit freigegeben werden.");
                }
            }

            if (ownsDatabaseConnection)
                await db.Database.CloseConnectionAsync();
        }
    }
}
