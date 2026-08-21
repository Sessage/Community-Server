using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;

namespace TodoSuite.Server.Services;

/// <summary>
/// Implementiert die Verwaltung von Aufgabenkommentaren.
/// </summary>
public class TodoCommentService : TodoWorkspaceServiceBase, ITodoCommentService
{
    private readonly INotificationService _notificationService;

    /// <summary>
    /// Erstellt eine neue Instanz der Kommentarverwaltung.
    /// </summary>
    public TodoCommentService(
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
    public async Task<TodoCommentEntity?> AddCommentAsync(
        string userId,
        Guid listId,
        Guid taskId,
        string message,
        CancellationToken cancellationToken = default,
        Guid? id = null)
    {
        var msg = (message ?? "").Trim();
        if (string.IsNullOrWhiteSpace(msg))
            throw new ArgumentException("Kommentar ist leer.", nameof(message));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken);

        if (list is null) return null;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Kommentar kann nicht erstellt werden (Liste='{list.Name}', User='{userId}').");

        if (id is { } requestedId && requestedId != Guid.Empty)
        {
            var existing = await db.TodoComments.AsNoTracking()
                .FirstOrDefaultAsync(comment => comment.Id == requestedId && comment.TaskId == taskId, cancellationToken);
            if (existing is not null) return existing;
        }

        var taskTitle = await db.TodoTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId && t.ListId == listId)
            .Select(t => t.Title)
            .FirstOrDefaultAsync(cancellationToken);

        if (taskTitle is null) return null;

        var authorName = (list.Participants
            .FirstOrDefault(p => EqualsUserKey(p.UserId, userId) || EqualsUserKey(p.Email, userId))?
            .DisplayName ?? "").Trim();

        if (string.IsNullOrWhiteSpace(authorName))
        {
            var (_, displayName) = await GetUserProfileAsync(db, userId, cancellationToken);
            authorName = (displayName ?? "").Trim();
        }

        if (string.IsNullOrWhiteSpace(authorName))
            authorName = userId;

        var entity = new TodoCommentEntity
        {
            Id = id is { } requestedIdValue && requestedIdValue != Guid.Empty ? requestedIdValue : Guid.NewGuid(),
            TaskId = taskId,
            Message = msg,
            Author = authorName,
            AuthorUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        db.Set<TodoCommentEntity>().Add(entity);

        await db.SaveChangesAsync(cancellationToken);
        await _notificationService.NotifyTaskEventAsync(
            userId,
            listId,
            taskId,
            NotificationEventType.CommentAdded,
            "Vorgang kommentiert",
            $"Ein Kommentar wurde zu \"{taskTitle}\" hinzugefuegt.",
            null,
            cancellationToken);
        await NotifyTaskUpdatesAsync(listId, taskId, cancellationToken);

        return new TodoCommentEntity
        {
            Id = entity.Id,
            Message = entity.Message,
            Author = entity.Author,
            AuthorUserId = entity.AuthorUserId,
            CreatedAt = entity.CreatedAt,
            TaskId = entity.TaskId
        };
    }

    /// <inheritdoc />
    public async Task<bool> RemoveCommentAsync(
        string userId,
        Guid listId,
        Guid taskId,
        Guid commentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken);

        if (list is null) return false;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Kommentar kann nicht entfernt werden (Liste='{list.Name}', User='{userId}').");

        var comment = await db.Set<TodoCommentEntity>()
            .FirstOrDefaultAsync(c => c.Id == commentId && c.TaskId == taskId, cancellationToken);

        if (comment is null) return false;

        var participantDisplayName = (list.Participants
            .FirstOrDefault(p => EqualsUserKey(p.UserId, userId) || EqualsUserKey(p.Email, userId))?
            .DisplayName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(participantDisplayName))
        {
            var (_, displayName) = await GetUserProfileAsync(db, userId, cancellationToken);
            participantDisplayName = (displayName ?? "").Trim();
        }

        var isOwnComment = EqualsUserKey(comment.AuthorUserId, userId)
                           || (string.IsNullOrWhiteSpace(comment.AuthorUserId)
                               && (EqualsUserKey(comment.Author, userId)
                                   || EqualsUserKey(comment.Author, participantDisplayName)));
        if (!isOwnComment)
            throw new UnauthorizedAccessException("Nur eigene Kommentare können gelöscht werden.");

        db.Remove(comment);
        await db.SaveChangesAsync(cancellationToken);

        await _notificationService.NotifyTaskEventAsync(
            userId,
            listId,
            taskId,
            NotificationEventType.CommentDeleted,
            "Vorgangskommentar gelöscht",
            "Ein Kommentar wurde gelöscht.",
            null,
            cancellationToken);

        await NotifyTaskUpdatesAsync(listId, taskId, cancellationToken);
        return true;
    }
}
