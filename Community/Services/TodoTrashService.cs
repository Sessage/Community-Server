using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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

    public TodoTrashService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoListEntity>> GetDeletedListsAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.TodoLists
            .Include(l => l.Tasks)
            .Include(l => l.Participants)
            .Where(l => l.DeletedAt != null &&
                        (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending && (p.Email == userId || p.UserId == userId))))
            .OrderByDescending(l => l.DeletedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoTaskEntity>> GetDeletedTasksAsync(string userId, Guid listId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        // Prüfen ob User Zugriff auf die Liste hat (auch gelöschte Listen)
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken);

        if (list is null)
            return Array.Empty<TodoTaskEntity>();

        if (!CanWrite(userId, list))
            return Array.Empty<TodoTaskEntity>();

        return await db.TodoTasks
            .Where(t => t.ListId == listId && t.DeletedAt != null)
            .OrderByDescending(t => t.DeletedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RestoreListAsync(string userId, Guid listId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt != null, cancellationToken);

        if (entity is null)
            return false;

        if (!CanAdmin(userId, entity))
            throw new UnauthorizedAccessException($"Liste '{entity.Name}' kann nicht wiederhergestellt werden (User='{userId}').");

        entity.DeletedAt = null;
        entity.DeletedByUserId = null;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyParticipantsListsUpdatedAsync(entity, cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RestoreTaskAsync(string userId, Guid listId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken);

        if (list is null)
            return false;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Aufgabe kann nicht wiederhergestellt werden (Liste='{list.Name}', User='{userId}').");

        var task = await db.TodoTasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.ListId == listId && t.DeletedAt != null, cancellationToken);

        if (task is null)
            return false;

        task.DeletedAt = null;
        task.DeletedByUserId = null;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var purged = 0;

        // Aufgaben endgültig löschen
        var expiredTasks = await db.TodoTasks
            .Include(t => t.Attachments)
            .Where(t => t.DeletedAt != null && t.DeletedAt < cutoff)
            .ToListAsync(cancellationToken);

        var attachmentIds = expiredTasks.SelectMany(t => t.Attachments).Select(a => a.Id).ToList();

        db.TodoTasks.RemoveRange(expiredTasks);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var attId in attachmentIds)
        {
            var diskPath = Path.Combine(UploadRoot, attId.ToString("N"));
            try { if (File.Exists(diskPath)) File.Delete(diskPath); } catch { }
        }

        purged += expiredTasks.Count;

        // Listen endgültig löschen (kaskadiert auf Aufgaben)
        var expiredLists = await db.TodoLists
            .Where(l => l.DeletedAt != null && l.DeletedAt < cutoff)
            .ToListAsync(cancellationToken);

        // Für jede abgelaufene Liste: Anhänge auf Disk löschen
        foreach (var list in expiredLists)
        {
            var listAttachmentIds = await db.TodoAttachments
                .Where(a => db.TodoTasks.Any(t => t.ListId == list.Id && t.Id == a.TaskId))
                .Select(a => a.Id)
                .ToListAsync(cancellationToken);

            foreach (var attId in listAttachmentIds)
            {
                var diskPath = Path.Combine(UploadRoot, attId.ToString("N"));
                try { if (File.Exists(diskPath)) File.Delete(diskPath); } catch { }
            }
        }

        db.TodoLists.RemoveRange(expiredLists);
        await db.SaveChangesAsync(cancellationToken);

        purged += expiredLists.Count;

        return purged;
    }
}
