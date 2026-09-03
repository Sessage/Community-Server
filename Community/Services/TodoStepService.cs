using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;

namespace TodoSuite.Server.Services;

/// <summary>
/// Manages ordered checklist steps belonging to a task, including completion state and reordering.
/// </summary>
public class TodoStepService : TodoWorkspaceServiceBase, ITodoStepService
{
    /// <summary>
    /// Erstellt eine neue Instanz der Schrittverwaltung.
    /// </summary>
    public TodoStepService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
    }

    /// <inheritdoc />
    public async Task<TodoStepEntity?> AddStepAsync(string userId, Guid listId, Guid taskId, string title, CancellationToken cancellationToken = default)
    {
        var t = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t))
            throw new ArgumentException("Schritt ist leer.", nameof(title));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return null;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Schritt kann nicht erstellt werden (Liste='{list.Name}', User='{userId}').");

        var taskExists = await db.TodoTasks
            .AsNoTracking()
            .AnyAsync(x => x.Id == taskId && x.ListId == listId && x.DeletedAt == null, cancellationToken);

        if (!taskExists) return null;

        var entity = new TodoStepEntity
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Title = t,
            IsCompleted = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        db.Set<TodoStepEntity>().Add(entity);

        await db.SaveChangesAsync(cancellationToken);
        await NotifyTaskUpdatesAsync(listId, taskId, cancellationToken);

        return new TodoStepEntity
        {
            Id = entity.Id,
            Title = entity.Title,
            IsCompleted = entity.IsCompleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            TaskId = entity.TaskId
        };
    }

    /// <inheritdoc />
    public async Task<bool> ToggleStepAsync(string userId, Guid listId, Guid taskId, Guid stepId, bool isCompleted, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return false;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Schritt kann nicht geändert werden (Liste='{list.Name}', User='{userId}').");

        var step = await db.Set<TodoStepEntity>()
            .FirstOrDefaultAsync(s => s.Id == stepId
                                      && s.TaskId == taskId
                                      && s.Task!.ListId == listId
                                      && s.Task.DeletedAt == null,
                cancellationToken);

        if (step is null) return false;

        step.IsCompleted = isCompleted;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyTaskUpdatesAsync(listId, taskId, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RenameStepAsync(string userId, Guid listId, Guid taskId, Guid stepId, string title, CancellationToken cancellationToken = default)
    {
        var t = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t))
            throw new ArgumentException("Schritt-Titel ist leer.", nameof(title));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return false;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Schritt kann nicht geändert werden (Liste='{list.Name}', User='{userId}').");

        var step = await db.Set<TodoStepEntity>()
            .FirstOrDefaultAsync(s => s.Id == stepId
                                      && s.TaskId == taskId
                                      && s.Task!.ListId == listId
                                      && s.Task.DeletedAt == null,
                cancellationToken);

        if (step is null) return false;

        step.Title = t;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyTaskUpdatesAsync(listId, taskId, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveStepAsync(string userId, Guid listId, Guid taskId, Guid stepId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return false;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Schritt kann nicht entfernt werden (Liste='{list.Name}', User='{userId}').");

        var step = await db.Set<TodoStepEntity>()
            .FirstOrDefaultAsync(s => s.Id == stepId
                                      && s.TaskId == taskId
                                      && s.Task!.ListId == listId
                                      && s.Task.DeletedAt == null,
                cancellationToken);

        if (step is null) return false;

        db.Remove(step);

        await db.SaveChangesAsync(cancellationToken);
        await NotifyTaskUpdatesAsync(listId, taskId, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<TodoTaskEntity?> ConvertStepToTaskAsync(string userId, Guid listId, Guid taskId, Guid stepId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.Tasks)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return null;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Schritt kann nicht konvertiert werden (Liste='{list.Name}', User='{userId}').");

        var step = await db.Set<TodoStepEntity>()
            .FirstOrDefaultAsync(s => s.Id == stepId
                                      && s.TaskId == taskId
                                      && s.Task!.ListId == listId
                                      && s.Task.DeletedAt == null,
                cancellationToken);

        if (step is null) return null;

        var title = step.Title;

        var targetCol = list.Columns.FirstOrDefault() ?? "Backlog";
        var nextListOrder = list.Tasks.Any() ? list.Tasks.Max(t => t.ListSortOrder) + 1 : 0;
        var nextKanbanOrder = list.Tasks.Where(t => t.Column == targetCol).Any()
            ? list.Tasks.Where(t => t.Column == targetCol).Max(t => t.KanbanSortOrder) + 1
            : 0;

        var newTask = new TodoTaskEntity
        {
            Id = Guid.NewGuid(),
            ListId = listId,
            Title = title,
            Column = targetCol,
            DueDate = DateTime.Today.AddDays(1),
            ListSortOrder = nextListOrder,
            KanbanSortOrder = nextKanbanOrder,
            CreatedAtUtc = DateTime.UtcNow
        };

        db.TodoTasks.Add(newTask);
        db.Remove(step);

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
        await NotifyTaskUpdatesAsync(listId, taskId, cancellationToken);

        return newTask;
    }
}
