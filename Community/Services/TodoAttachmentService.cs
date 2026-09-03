using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;

namespace TodoSuite.Server.Services;

/// <summary>
/// Implementiert die Verwaltung von Aufgabenanhängen.
/// </summary>
public class TodoAttachmentService : TodoWorkspaceServiceBase, ITodoAttachmentService
{
    private const long MaxAttachmentSizeBytes = 25L * 1024 * 1024;
    private static readonly SemaphoreSlim[] AttachmentLocks = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly INotificationService _notificationService;

    /// <summary>
    /// Erstellt eine neue Instanz der Anhangsverwaltung.
    /// </summary>
    public TodoAttachmentService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService,
        INotificationService notificationService)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
        _notificationService = notificationService;
    }

    /// <inheritdoc />
    public async Task<TodoAttachmentEntity?> AddAttachmentAsync(
        string userId,
        Guid listId,
        Guid taskId,
        string originalFileName,
        Stream content,
        CancellationToken cancellationToken = default,
        Guid? id = null)
    {
        var safeName = SafeFileName(originalFileName);

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return null;
        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Anhang kann nicht hochgeladen werden (Liste='{list.Name}', User='{userId}').");

        var task = await db.TodoTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId && t.ListId == listId && t.DeletedAt == null, cancellationToken);

        if (task is null) return null;

        var attachmentId = id ?? Guid.NewGuid();
        var attachmentLock = GetAttachmentLock(attachmentId);
        await attachmentLock.WaitAsync(cancellationToken);
        try
        {
            return await AddAttachmentCoreAsync(
                db,
                userId,
                listId,
                task,
                attachmentId,
                safeName,
                content,
                cancellationToken);
        }
        finally
        {
            attachmentLock.Release();
        }
    }

    private async Task<TodoAttachmentEntity> AddAttachmentCoreAsync(
        ApplicationDbContext db,
        string userId,
        Guid listId,
        TodoTaskEntity task,
        Guid attachmentId,
        string safeName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var existing = await db.TodoAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
        if (existing is not null)
        {
            if (existing.TaskId != task.Id)
                throw new InvalidOperationException("Die Anhangs-ID wird bereits für eine andere Aufgabe verwendet.");
            return existing;
        }

        var storedFileName = attachmentId.ToString("N");
        var diskPath = Path.Combine(UploadRoot, storedFileName);

        try
        {
            await using var fs = File.Create(diskPath);
            await CopyToWithLimitAsync(content, fs, MaxAttachmentSizeBytes, cancellationToken);
        }
        catch
        {
            try { if (File.Exists(diskPath)) File.Delete(diskPath); } catch { }
            throw;
        }

        var url = $"/api/attachments/{attachmentId}?listId={listId}";

        var entity = new TodoAttachmentEntity
        {
            Id = attachmentId,
            TaskId = task.Id,
            FileName = safeName,
            Url = url
        };

        db.Set<TodoAttachmentEntity>().Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(entity).State = EntityState.Detached;
            var existingAfterRace = await db.TodoAttachments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TaskId == task.Id, cancellationToken);
            if (existingAfterRace is not null)
                return existingAfterRace;
            try { if (File.Exists(diskPath)) File.Delete(diskPath); } catch { }
            throw;
        }

        await NotifyTaskUpdatesAsync(listId, task.Id, cancellationToken);
        await _notificationService.NotifyTaskEventAsync(
            userId,
            listId,
            task.Id,
            NotificationEventType.AttachmentAdded,
            "Neuer Anhang",
            $"Der Anhang \"{safeName}\" wurde hinzugefuegt.",
            task.Assignee,
            cancellationToken);
        return new TodoAttachmentEntity
        {
            Id = entity.Id,
            FileName = entity.FileName,
            Url = entity.Url,
            TaskId = entity.TaskId
        };
    }

    /// <inheritdoc />
    public async Task<(Stream Stream, string FileName)?> GetAttachmentStreamAsync(
        string userId,
        Guid listId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return null;
        if (!CanRead(userId, list))
            throw new UnauthorizedAccessException($"Anhang kann nicht heruntergeladen werden (Liste='{list.Name}', User='{userId}').");

        var attachment = await db.TodoAttachments
            .AsNoTracking()
            .Where(a => a.Id == attachmentId)
            .Join(db.TodoTasks, a => a.TaskId, t => t.Id, (a, t) => new { a, t })
            .Where(x => x.t.ListId == listId && x.t.DeletedAt == null)
            .Select(x => x.a)
            .FirstOrDefaultAsync(cancellationToken);

        if (attachment is null) return null;

        var diskPath = Path.Combine(UploadRoot, attachmentId.ToString("N"));
        if (!File.Exists(diskPath)) return null;

        var stream = File.OpenRead(diskPath);
        return (stream, attachment.FileName);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAttachmentAsync(string userId, Guid listId, Guid taskId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return false;
        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Anhang kann nicht entfernt werden (Liste='{list.Name}', User='{userId}').");

        TodoAttachmentEntity? attachment;
        var attachmentLock = GetAttachmentLock(attachmentId);
        await attachmentLock.WaitAsync(cancellationToken);
        try
        {
            attachment = await db.TodoAttachments
                .AsNoTracking()
                .Where(a => a.Id == attachmentId)
                .Join(db.TodoTasks, a => a.TaskId, t => t.Id, (a, t) => new { a, t })
                .Where(x => x.t.ListId == listId && x.t.Id == taskId && x.t.DeletedAt == null)
                .Select(x => x.a)
                .FirstOrDefaultAsync(cancellationToken);

            if (attachment is null) return false;

            db.Remove(attachment);
            await db.SaveChangesAsync(cancellationToken);

            var diskPath = Path.Combine(UploadRoot, attachmentId.ToString("N"));
            try { if (File.Exists(diskPath)) File.Delete(diskPath); } catch { }
        }
        finally
        {
            attachmentLock.Release();
        }

        await NotifyTaskUpdatesAsync(listId, taskId, cancellationToken);
        await _notificationService.NotifyTaskEventAsync(
            userId,
            listId,
            taskId,
            NotificationEventType.AttachmentDeleted,
            "Anhang gelöscht",
            $"Der Anhang \"{attachment.FileName}\" wurde gelöscht.",
            null,
            cancellationToken);
        return true;
    }

    private static SemaphoreSlim GetAttachmentLock(Guid attachmentId)
        => AttachmentLocks[(int)((uint)attachmentId.GetHashCode() % (uint)AttachmentLocks.Length)];

    private static async Task CopyToWithLimitAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException($"Anhang ist zu gross. Maximal erlaubt sind {maxBytes / 1024 / 1024} MB.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
