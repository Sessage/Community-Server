using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;
using Klassenbibliothek.Features;

namespace TodoSuite.Server.Services;

/// <summary>
/// Implements task lifecycle operations and preserves list ordering, revision metadata, and notifications.
/// Mutations verify access to the owning list before touching the task.
/// </summary>
public class TodoTaskService : TodoWorkspaceServiceBase, ITodoTaskService
{
    private readonly INotificationService _notificationService;
    private readonly ITodoAutomationService _automationService;
    private readonly IProductFeatureCatalog _features;
    private bool CustomFieldsEnabled => _features.IsEnabled(ProductFeatureIds.Forms);

    /// <summary>
    /// Erstellt eine neue Instanz der Aufgabenverwaltung.
    /// </summary>
    public TodoTaskService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService,
        INotificationService notificationService,
        ITodoAutomationService automationService,
        IProductFeatureCatalog features)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
        _notificationService = notificationService;
        _automationService = automationService;
        _features = features;
    }

    /// <inheritdoc />
    public async Task<TodoTaskEntity?> AddTaskAsync(string userId, Guid listId, TodoTaskEntity task, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            return null;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException(
                $"Aufgabe kann nicht angelegt werden (Liste='{list.Name}', User='{userId}').");

        if (task.Id != Guid.Empty)
        {
            var existing = await db.TodoTasks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == task.Id && t.ListId == listId && t.DeletedAt == null, cancellationToken);

            if (existing is not null)
                return existing;
        }

        var targetCol = TodoTaskInputValidation.ResolveColumn(list, task.Column);
        var assignee = TodoTaskInputValidation.ResolveAssignee(list, task.Assignee, nameof(task));

        var nextListOrder = (await db.TodoTasks
            .Where(t => t.ListId == listId && t.DeletedAt == null && !t.Done)
            .Select(t => (int?)t.ListSortOrder)
            .MaxAsync(cancellationToken) ?? -1) + 1;

        var nextKanbanOrder = (await db.TodoTasks
            .Where(t => t.ListId == listId && t.DeletedAt == null && !t.Done && t.Column == targetCol)
            .Select(t => (int?)t.KanbanSortOrder)
            .MaxAsync(cancellationToken) ?? -1) + 1;

        var entity = new TodoTaskEntity
        {
            Id = task.Id == Guid.Empty ? Guid.NewGuid() : task.Id,
            ListId = listId,
            Title = (task.Title ?? "").Trim(),
            Description = RichTextContent.NormalizeForStorage(task.Description),
            StartDate = task.StartDate,
            DueDate = task.DueDate,
            Done = task.Done,
            IsImportant = task.IsImportant,
            Assignee = assignee,
            Recurrence = task.Recurrence,
            CustomRecurrence = task.CustomRecurrence,
            Column = targetCol,
            ReminderAtUtc = task.ReminderAtUtc,
            ReminderSentAtUtc = null,
            ListSortOrder = nextListOrder,
            KanbanSortOrder = nextKanbanOrder,
            CreatedAtUtc = DateTime.UtcNow
        };

        if (CustomFieldsEnabled)
            ApplyCustomFieldValues(entity, NormalizeCustomFieldValues(task.CustomFieldValues, await GetCustomFieldDefinitionsAsync(db, listId, cancellationToken)));

        if (string.IsNullOrWhiteSpace(entity.Title))
            throw new ArgumentException(
                $"Aufgabe konnte nicht angelegt werden: Titel ist leer. ListId='{listId}'.",
                nameof(task));

        db.TodoTasks.Add(entity);

        await db.SaveChangesAsync(cancellationToken);

        await _notificationService.NotifyTaskEventAsync(
            userId,
            listId,
            entity.Id,
            NotificationEventType.TaskCreated,
            "Vorgang erstellt",
            $"Die Aufgabe \"{entity.Title}\" wurde erstellt.",
            entity.Assignee,
            cancellationToken);

        await _automationService.ExecuteAsync(
            new TodoAutomationContext(listId, list.Name, userId, entity, null, TodoAutomationTriggerType.TaskCreated),
            cancellationToken);
        await db.Entry(entity).ReloadAsync(cancellationToken);

        await NotifyListUpdatedAsync(listId, cancellationToken);

        return entity;
    }

    /// <inheritdoc />
    public async Task<TodoTaskEntity?> UpdateTaskAsync(string userId, Guid listId, TodoTaskEntity task, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            return null;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Aufgabe kann nicht geändert werden (Liste='{list.Name}', User='{userId}').");

        var entity = await db.TodoTasks
            .Include(t => t.Attachments)
            .Include(t => t.Steps)
            .Include(t => t.Comments)
            .Include(t => t.Members)
            .Include(t => t.Watchers)
            .Include(t => t.LabelLinks)
            .Include(t => t.CustomFieldValues)
            .FirstOrDefaultAsync(t => t.Id == task.Id && t.ListId == listId && t.DeletedAt == null, cancellationToken);

        if (entity is null)
            return null;

        // Mobile full updates carry the version they read. Reject stale versions before copying
        // any fields so a delayed offline write cannot silently replace a newer edit.
        if (task.SyncVersion.HasValue && task.SyncVersion.Value != entity.ContentVersion)
            throw new WorkspaceConcurrencyException("Die Aufgabe wurde zwischenzeitlich auf einem anderen Gerät geändert.");
        entity.ContentVersion++;

        var previousTask = SnapshotForAutomation(entity);

        entity.Title = (task.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(entity.Title))
            throw new ArgumentException($"Aufgabe konnte nicht geändert werden: Titel ist leer. TaskId='{entity.Id}'.", nameof(task));

        var wasAlreadyDone = entity.Done;
        var oldAssignee = entity.Assignee;

        entity.Description = RichTextContent.NormalizeForStorage(task.Description);
        entity.StartDate = task.StartDate;
        entity.DueDate = task.DueDate;
        entity.Done = task.Done;
        entity.IsImportant = task.IsImportant;
        entity.Assignee = TodoTaskInputValidation.ResolveAssignee(list, task.Assignee, nameof(task));
        entity.Recurrence = task.Recurrence;
        entity.CustomRecurrence = task.CustomRecurrence;
        entity.Column = TodoTaskInputValidation.ResolveColumn(list, task.Column);
        entity.CardColor = string.IsNullOrWhiteSpace(task.CardColor) ? null : task.CardColor.Trim();
        entity.CardColorMode = task.CardColorMode;
        var customFieldValues = CustomFieldsEnabled
            ? NormalizeCustomFieldValues(task.CustomFieldValues, await GetCustomFieldDefinitionsAsync(db, listId, cancellationToken))
            : [];

        var incomingMemberIds = (task.MemberUserIds ?? new List<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Task membership cannot grant access. Accept only stable IDs of already accepted list
        // participants; legacy email-shaped values are deliberately rejected for new writes.
        var eligibleMemberIds = (list.Participants ?? [])
            .Where(participant => !participant.InvitationPending && !string.IsNullOrWhiteSpace(participant.UserId))
            .Select(participant => participant.UserId!.Trim())
            .Where(memberId => !memberId.Contains('@'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalidMembers = incomingMemberIds
            .Where(memberId => memberId.Contains('@') || !eligibleMemberIds.Contains(memberId))
            .ToList();
        if (invalidMembers.Count > 0)
            throw new ArgumentException($"Nicht berechtigte Aufgabenmitglieder: {string.Join(", ", invalidMembers)}", nameof(task));

        entity.Members ??= [];
        var removedMembers = entity.Members
            .Where(member => !incomingMemberIds.Contains(member.UserId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        db.TodoTaskMembers.RemoveRange(removedMembers);
        foreach (var member in removedMembers) entity.Members.Remove(member);
        var existingMemberIds = entity.Members.Select(member => member.UserId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var memberId in incomingMemberIds.Where(memberId => !existingMemberIds.Contains(memberId)))
        {
            var member = new TodoTaskMemberEntity { Id = Guid.NewGuid(), TaskId = entity.Id, UserId = memberId };
            entity.Members.Add(member);
            db.TodoTaskMembers.Add(member);
        }

        // Validate every label against the parent list before replacing the join collection.
        // This prevents cross-list relationships when clients submit arbitrary GUIDs.
        var validLabelIds = await db.TodoLabels
            .Where(x => x.ListId == listId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var validSet = new HashSet<Guid>(validLabelIds);

        var incomingIds = (task.LabelLinks ?? new List<TodoTaskLabelEntity>())
            .Select(x => x.LabelId)
            .Distinct()
            .ToList();

        var invalid = incomingIds.Where(id => !validSet.Contains(id)).ToList();
        if (invalid.Count > 0)
            throw new ArgumentException(
                $"Aufgabe konnte nicht geändert werden: Unbekannte Label-Ids: {string.Join(", ", invalid)}. TaskId='{entity.Id}'.",
                nameof(task));

        entity.LabelLinks ??= new List<TodoTaskLabelEntity>();
        entity.LabelLinks.Clear();

        foreach (var id in incomingIds)
        {
            entity.LabelLinks.Add(new TodoTaskLabelEntity
            {
                TaskId = entity.Id,
                LabelId = id
            });
        }

        var stepCreatedAtById = entity.Steps
            .ToDictionary(step => step.Id, step => step.CreatedAtUtc);
        entity.Steps.Clear();
        foreach (var step in task.Steps ?? new List<TodoStepEntity>())
        {
            var title = (step.Title ?? "").Trim();
            if (string.IsNullOrWhiteSpace(title))
                continue;

            entity.Steps.Add(new TodoStepEntity
            {
                Id = step.Id == Guid.Empty ? Guid.NewGuid() : step.Id,
                Title = title,
                IsCompleted = step.IsCompleted,
                CreatedAtUtc = stepCreatedAtById.TryGetValue(step.Id, out var existingCreatedAt)
                    ? existingCreatedAt
                    : step.CreatedAtUtc,
                TaskId = entity.Id
            });
        }
        var oldReminderAtUtc = entity.ReminderAtUtc;
        entity.ReminderAtUtc = task.ReminderAtUtc;

        // A changed schedule represents a new reminder occurrence and must clear the previous
        // delivery marker; otherwise the dispatcher would suppress it as already sent.
        if (oldReminderAtUtc != entity.ReminderAtUtc)
            entity.ReminderSentAtUtc = null;

        if (entity.Done)
        {
            entity.ReminderAtUtc = null;
            entity.ReminderSentAtUtc = null;

            var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(entity.Assignee))
                recipients.Add(entity.Assignee.Trim());

            foreach (var m in entity.Members ?? new())
                recipients.Add(m.UserId.Trim());

            foreach (var r in recipients)
            {
                await HubContext.Clients
                    .Group(TodoHub.UserGroup(r))
                    .SendAsync(TodoHub.ReminderTriggered,
                        "Aufgabe abgeschlossen",
                        $"Die Aufgabe „{entity.Title}“ wurde abgeschlossen.",
                        entity.Id,
                        cancellationToken);
            }
        }

        if (CustomFieldsEnabled)
            await ApplyCustomFieldValuesAsync(db, entity.Id, customFieldValues, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new WorkspaceConcurrencyException("Die Aufgabe wurde gleichzeitig auf einem anderen Gerät geändert.");
        }

        // Run automations only after the initiating mutation is durable. Rule failures therefore
        // cannot roll back the user's edit, and trigger comparisons use the pre-save snapshot.
        if (!string.Equals(previousTask.Column, entity.Column, StringComparison.OrdinalIgnoreCase))
        {
            await _automationService.ExecuteAsync(
                new TodoAutomationContext(listId, list.Name, userId, entity, previousTask, TodoAutomationTriggerType.ColumnChanged),
                cancellationToken);
        }

        if (!previousTask.Done && entity.Done)
        {
            await _automationService.ExecuteAsync(
                new TodoAutomationContext(listId, list.Name, userId, entity, previousTask, TodoAutomationTriggerType.TaskCompleted),
                cancellationToken);
        }
        else if (previousTask.Done && !entity.Done)
        {
            await _automationService.ExecuteAsync(
                new TodoAutomationContext(listId, list.Name, userId, entity, previousTask, TodoAutomationTriggerType.TaskReopened),
                cancellationToken);
        }

        if (!string.Equals(previousTask.Assignee, entity.Assignee, StringComparison.OrdinalIgnoreCase))
        {
            await _automationService.ExecuteAsync(
                new TodoAutomationContext(listId, list.Name, userId, entity, previousTask, TodoAutomationTriggerType.AssigneeChanged),
                cancellationToken);
        }

        // Process the concrete user transitions before the generic update. A generic
        // automation may itself change column, completion or assignee and already emits
        // the corresponding follow-up trigger. Running it first would then replay the
        // original transition with stale pre-update state.
        await _automationService.ExecuteAsync(
            new TodoAutomationContext(listId, list.Name, userId, entity, previousTask, TodoAutomationTriggerType.TaskUpdated),
            cancellationToken);

        await db.Entry(entity).ReloadAsync(cancellationToken);

        var newAssignee = entity.Assignee;
        if (!string.Equals((oldAssignee ?? "").Trim(), (newAssignee ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(newAssignee))
        {
            await _notificationService.NotifyTaskEventAsync(
                userId,
                listId,
                entity.Id,
                NotificationEventType.TaskAssigned,
                "Vorgang zugewiesen",
                $"Die Aufgabe \"{entity.Title}\" wurde zugewiesen.",
                newAssignee,
                cancellationToken);
        }
        else
        {
            await _notificationService.NotifyTaskEventAsync(
                userId,
                listId,
                entity.Id,
                entity.Done && !wasAlreadyDone
                    ? NotificationEventType.TaskCompleted
                    : !entity.Done && wasAlreadyDone
                        ? NotificationEventType.TaskReopened
                        : NotificationEventType.TaskUpdated,
                entity.Done && !wasAlreadyDone
                    ? "Vorgang erledigt"
                    : !entity.Done && wasAlreadyDone
                        ? "Vorgang erneut geöffnet"
                        : "Vorgang aktualisiert",
                $"Die Aufgabe \"{entity.Title}\" wurde geändert.",
                entity.Assignee,
                cancellationToken);
        }

        // Wiederholung: Wenn Aufgabe soeben abgeschlossen wurde und ein Wiederholungsintervall hat,
        // wird eine neue Aufgabe mit der berechneten Fälligkeit erstellt.
        if (entity.Done && !wasAlreadyDone && entity.Recurrence != RecurrencePattern.Keine)
        {
            var baseDate = (entity.DueDate ?? DateTime.UtcNow).Date;
            var nextDueDate = CalculateNextDueDate(entity.Recurrence, baseDate);

            if (nextDueDate.HasValue)
            {
                var activeTasks = await db.TodoTasks
                    .Where(t => t.ListId == entity.ListId && t.DeletedAt == null && !t.Done)
                    .ToListAsync(cancellationToken);

                var newTask = new TodoTaskEntity
                {
                    Id = Guid.NewGuid(),
                    ListId = entity.ListId,
                    Title = entity.Title,
                    Description = entity.Description,
                    DueDate = nextDueDate,
                    Done = false,
                    IsImportant = entity.IsImportant,
                    Assignee = entity.Assignee,
                    Recurrence = entity.Recurrence,
                    CustomRecurrence = entity.CustomRecurrence,
                    Column = entity.Column,
                    CardColor = entity.CardColor,
                    CardColorMode = entity.CardColorMode,
                    CreatedAtUtc = DateTime.UtcNow,
                    ListSortOrder = activeTasks.Any() ? activeTasks.Max(t => t.ListSortOrder) + 1 : 0,
                    KanbanSortOrder = activeTasks.Where(t => t.Column == entity.Column).Any()
                        ? activeTasks.Where(t => t.Column == entity.Column).Max(t => t.KanbanSortOrder) + 1
                        : 0,
                    LabelLinks = (entity.LabelLinks ?? new List<TodoTaskLabelEntity>())
                        .Select(ll => new TodoTaskLabelEntity { TaskId = Guid.Empty, LabelId = ll.LabelId })
                        .ToList(),
                    Steps = (entity.Steps ?? new List<TodoStepEntity>())
                        .Select(s => new TodoStepEntity { Id = Guid.NewGuid(), Title = s.Title, IsCompleted = false })
                        .ToList(),
                    Members = (entity.Members ?? new List<TodoTaskMemberEntity>())
                        .Select(m => new TodoTaskMemberEntity { UserId = m.UserId })
                        .ToList(),
                    CustomFieldValues = customFieldValues
                        .Select(v => new TodoTaskCustomFieldValueEntity { Id = Guid.NewGuid(), FieldId = v.FieldId, Value = v.Value })
                        .ToList()
                };

                // TaskId in den abhängigen Entitäten korrekt setzen
                foreach (var ll in newTask.LabelLinks) ll.TaskId = newTask.Id;
                foreach (var s in newTask.Steps) s.TaskId = newTask.Id;
                foreach (var m in newTask.Members) m.TaskId = newTask.Id;
                foreach (var v in newTask.CustomFieldValues) v.TaskId = newTask.Id;

                db.TodoTasks.Add(newTask);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        await NotifyListUpdatedAsync(listId, cancellationToken);
        await NotifyTaskUpdatesAsync(listId, entity.Id, cancellationToken);

        entity.MemberUserIds = incomingMemberIds.ToList();
        entity.SyncVersion = entity.ContentVersion;

        return entity;
    }

    /// <inheritdoc />
    public async Task<TodoTaskEntity?> DecideApprovalAsync(
        string userId,
        Guid listId,
        Guid taskId,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);
        if (list is null)
            return null;

        var task = await db.TodoTasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.ListId == listId && t.DeletedAt == null, cancellationToken);
        if (task is null)
            return null;

        var normalizedUserId = (userId ?? string.Empty).Trim();
        var isAcceptedParticipant = string.Equals(list.OwnerId, normalizedUserId, StringComparison.OrdinalIgnoreCase)
            || list.Participants.Any(p => !p.InvitationPending
                && string.Equals(p.UserId, normalizedUserId, StringComparison.OrdinalIgnoreCase));
        if (!isAcceptedParticipant
            || !string.Equals(task.ApproverUserId, normalizedUserId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Nur der ausgewählte Genehmiger darf diese Entscheidung treffen.");
        if (task.ApprovalStatus != TodoApprovalStatus.Pending)
            throw new InvalidOperationException("Für diese Aufgabe steht keine Genehmigung aus.");

        var previousTask = SnapshotForAutomation(task);
        task.ApprovalStatus = approved ? TodoApprovalStatus.Approved : TodoApprovalStatus.Rejected;
        task.ApprovalDecisionAtUtc = DateTime.UtcNow;
        task.ApprovalDecisionByUserId = normalizedUserId;
        task.ContentVersion++;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new WorkspaceConcurrencyException("Die Genehmigung wurde zwischenzeitlich bereits bearbeitet.");
        }

        var trigger = approved
            ? TodoAutomationTriggerType.ApprovalGranted
            : TodoAutomationTriggerType.ApprovalRejected;
        await _automationService.ExecuteAsync(
            new TodoAutomationContext(listId, list.Name, normalizedUserId, task, previousTask, trigger),
            cancellationToken);

        await _notificationService.NotifyTaskEventAsync(
            normalizedUserId,
            listId,
            task.Id,
            approved ? NotificationEventType.ApprovalGranted : NotificationEventType.ApprovalRejected,
            approved ? "Genehmigung erfolgt" : "Genehmigung abgelehnt",
            approved
                ? $"Die Aufgabe „{task.Title}“ wurde genehmigt."
                : $"Die Aufgabe „{task.Title}“ wurde abgelehnt.",
            task.Assignee,
            cancellationToken);

        await NotifyListUpdatedAsync(listId, cancellationToken);
        await NotifyTaskUpdatesAsync(listId, task.Id, cancellationToken);

        var result = await db.TodoTasks
            .AsNoTracking()
            .Include(t => t.Attachments)
            .Include(t => t.Steps)
            .Include(t => t.Comments)
            .Include(t => t.Members)
            .Include(t => t.Watchers)
            .Include(t => t.LabelLinks)
            .Include(t => t.CustomFieldValues)
            .FirstAsync(t => t.Id == taskId && t.ListId == listId, cancellationToken);
        result.MemberUserIds = result.Members.Select(member => member.UserId).ToList();
        result.SyncVersion = result.ContentVersion;
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTaskAsync(string userId, Guid listId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            return false;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Aufgabe kann nicht gelöscht werden (Liste='{list.Name}', User='{userId}').");

        var task = await db.TodoTasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.ListId == listId && t.DeletedAt == null, cancellationToken);

        if (task is null)
            return false;

        // Soft-Delete: In Papierkorb verschieben
        task.DeletedAt = DateTime.UtcNow;
        task.DeletedByUserId = userId;
        task.ContentVersion++;

        await db.SaveChangesAsync(cancellationToken);

        await _notificationService.NotifyTaskEventAsync(
            userId,
            listId,
            taskId,
            NotificationEventType.TaskDeleted,
            "Vorgang gelöscht",
            $"Die Aufgabe \"{task.Title}\" wurde gelöscht.",
            task.Assignee,
            cancellationToken);

        await NotifyListUpdatedAsync(listId, cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<TodoTaskEntity?> MoveTaskToListAsync(
        string userId,
        Guid fromListId,
        Guid toListId,
        Guid taskId,
        string? desiredTargetColumn = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var fromList = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == fromListId && l.DeletedAt == null && !l.IsTemplate, cancellationToken);

        if (fromList is null)
            return null;

        var toList = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.Labels)
            .FirstOrDefaultAsync(l => l.Id == toListId && l.DeletedAt == null && !l.IsTemplate, cancellationToken);

        if (toList is null)
            return null;

        if (!CanWrite(userId, fromList))
            throw new UnauthorizedAccessException(
                $"Verschieben nicht erlaubt: Keine Schreibrechte in Quell-Liste '{fromList.Name}'. User='{userId}'.");

        if (!CanWrite(userId, toList))
            throw new UnauthorizedAccessException(
                $"Verschieben nicht erlaubt: Keine Schreibrechte in Ziel-Liste '{toList.Name}'. User='{userId}'.");

        var task = await db.TodoTasks
            .Include(t => t.Attachments)
            .Include(t => t.Steps)
            .Include(t => t.Comments)
            .Include(t => t.LabelLinks)
            .ThenInclude(ll => ll.Label)
            .Include(t => t.CustomFieldValues)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.ListId == fromListId && t.DeletedAt == null, cancellationToken);

        if (task is null)
            return null;

        if (fromListId == toListId)
            return task;

        var previousTask = SnapshotForAutomation(task);

        toList.Columns ??= new List<string>();

        string fallbackCol = toList.Columns.FirstOrDefault() ?? "Backlog";

        var wanted = (desiredTargetColumn ?? "").Trim();
        string targetColumn;

        if (!string.IsNullOrWhiteSpace(wanted) && toList.Columns.Any(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            targetColumn = toList.Columns.First(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        else
            targetColumn = fallbackCol;

        var nextListOrder = (await db.TodoTasks
            .Where(t => t.ListId == toListId && !t.Done && t.DeletedAt == null)
            .Select(t => (int?)t.ListSortOrder)
            .MaxAsync(cancellationToken) ?? -1) + 1;

        var nextKanbanOrder = (await db.TodoTasks
            .Where(t => t.ListId == toListId && !t.Done && t.DeletedAt == null && t.Column == targetColumn)
            .Select(t => (int?)t.KanbanSortOrder)
            .MaxAsync(cancellationToken) ?? -1) + 1;

        var toLabelsByTitle = (toList.Labels ?? new List<TodoLabelEntity>())
            .GroupBy(l => (l.Title ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var oldTitles = (task.LabelLinks ?? new List<TodoTaskLabelEntity>())
            .Select(ll => ll.Label?.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        task.LabelLinks ??= new List<TodoTaskLabelEntity>();
        task.LabelLinks.Clear();

        foreach (var title in oldTitles)
        {
            if (toLabelsByTitle.TryGetValue(title, out var targetLabel))
            {
                task.LabelLinks.Add(new TodoTaskLabelEntity
                {
                    TaskId = task.Id,
                    LabelId = targetLabel.Id
                });
            }
        }

        foreach (var att in task.Attachments ?? new List<TodoAttachmentEntity>())
        {
            if (!string.IsNullOrWhiteSpace(att.Url))
            {
                var baseUrl = att.Url.Split('?', 2)[0];
                att.Url = $"{baseUrl}?listId={toListId}";
            }
        }

        task.ListId = toListId;
        task.Column = targetColumn;
        task.Done = (toList.DoneColumns ?? []).Contains(targetColumn, StringComparer.OrdinalIgnoreCase);
        if (task.Done)
            task.ReminderAtUtc = null;
        task.ListSortOrder = nextListOrder;
        task.KanbanSortOrder = nextKanbanOrder;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new WorkspaceConcurrencyException("Die Aufgabe wurde während des Verschiebens auf einem anderen Gerät geändert.");
        }

        var persistedListId = await db.TodoTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.ListId)
            .FirstOrDefaultAsync(cancellationToken);

        if (persistedListId != toListId)
        {
            throw new WorkspaceConcurrencyException(
                "Die Aufgabe konnte wegen einer gleichzeitigen Änderung nicht verschoben werden.");
        }

        await _automationService.ExecuteAsync(
            new TodoAutomationContext(toListId, toList.Name, userId, task, previousTask, TodoAutomationTriggerType.ColumnChanged),
            cancellationToken);
        await db.Entry(task).ReloadAsync(cancellationToken);

        await _notificationService.NotifyTaskEventAsync(
            userId,
            toListId,
            task.Id,
            NotificationEventType.TaskMoved,
            "Vorgang verschoben",
            $"Die Aufgabe \"{task.Title}\" wurde verschoben.",
            task.Assignee,
            cancellationToken);

        await NotifyListUpdatedAsync(fromListId, cancellationToken);
        await NotifyListUpdatedAsync(toListId, cancellationToken);

        return task;
    }

    /// <inheritdoc />
    public async Task ReorderListAsync(string userId, Guid listId, IReadOnlyList<Guid> orderedTaskIds, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            return;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Reihenfolge (Liste) kann nicht gespeichert werden (Liste='{list.Name}', User='{userId}').");

        var open = await db.TodoTasks
            .Where(t => t.ListId == listId && !t.Done && t.DeletedAt == null)
            .ToListAsync(cancellationToken);
        var openDict = open.ToDictionary(t => t.Id);

        var seen = new HashSet<Guid>();
        var i = 0;

        if (orderedTaskIds is not null)
        {
            foreach (var id in orderedTaskIds)
            {
                // Doppelte oder fremde IDs dürfen weder Lücken erzeugen noch dieselbe
                // Aufgabe mehrfach neu nummerieren. Der Endpunkt bleibt damit auch bei
                // einem beschädigten/veralteten DOM-Snapshot deterministisch.
                if (!openDict.TryGetValue(id, out var t) || !seen.Add(id))
                    continue;

                t.ListSortOrder = i++;
            }
        }

        var rest = open
            .Where(t => !seen.Contains(t.Id))
            .OrderBy(t => t.ListSortOrder)
            .ThenBy(t => t.CreatedAtUtc)
            .ToList();

        foreach (var t in rest)
            t.ListSortOrder = i++;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }

    /// Berechnet das nächste Fälligkeitsdatum basierend auf dem Wiederholungsintervall.
    private static DateTime? CalculateNextDueDate(RecurrencePattern recurrence, DateTime baseDate)
        => recurrence switch
        {
            RecurrencePattern.Taeglich            => baseDate.AddDays(1),
            RecurrencePattern.Woechentlich        => baseDate.AddDays(7),
            RecurrencePattern.BestimmteWochentage => baseDate.AddDays(7),
            RecurrencePattern.Monatlich           => baseDate.AddMonths(1),
            RecurrencePattern.Jaehrlich           => baseDate.AddYears(1),
            _                                     => null // Keine, Benutzerdefiniert
        };

    private static async Task<Dictionary<Guid, TodoCustomFieldDefinitionEntity>> GetCustomFieldDefinitionsAsync(ApplicationDbContext db, Guid listId, CancellationToken ct)
        => (await db.TodoCustomFields
            .Include(x => x.SourceTaskList)!.ThenInclude(l => l!.Tasks)
            .Where(x => x.ListId == listId)
            .ToListAsync(ct))
            .ToDictionary(x => x.Id);

    private static IReadOnlyList<NormalizedCustomFieldValue> NormalizeCustomFieldValues(
        IEnumerable<TodoTaskCustomFieldValueEntity>? incoming,
        IReadOnlyDictionary<Guid, TodoCustomFieldDefinitionEntity> customFields)
        => (incoming ?? [])
            .Where(v => customFields.ContainsKey(v.FieldId))
            .GroupBy(v => v.FieldId)
            .Select(g => g.Last())
            .Select(v => new NormalizedCustomFieldValue(v.FieldId, NormalizeCustomFieldValue(customFields[v.FieldId], v.Value)))
            .Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .ToList();

    private static string NormalizeCustomFieldValue(TodoCustomFieldDefinitionEntity field, string? value)
    {
        var normalized = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return normalized;

        if (field.Type == TodoCustomFieldType.Number
            && !decimal.TryParse(normalized, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new ArgumentException($"Das benutzerdefinierte Feld „{field.Name}“ benötigt eine Zahl.");
        if (field.Type == TodoCustomFieldType.Date
            && !DateOnly.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            throw new ArgumentException($"Das benutzerdefinierte Feld „{field.Name}“ benötigt ein gültiges Datum.");
        if (field.Type == TodoCustomFieldType.Checkbox && !bool.TryParse(normalized, out _))
            throw new ArgumentException($"Das benutzerdefinierte Feld „{field.Name}“ benötigt Ja oder Nein.");
        if (field.Type is TodoCustomFieldType.Dropdown or TodoCustomFieldType.MultiSelect
            && !CustomFieldSelectOptions.ContainsValue(field, normalized))
            throw new ArgumentException($"Das benutzerdefinierte Feld „{field.Name}“ enthält eine ungültige Auswahl.");

        if (field.Type == TodoCustomFieldType.MultiSelect)
            return CustomFieldMultiSelectValues.Serialize(CustomFieldMultiSelectValues.Parse(normalized));

        if (field.Type != TodoCustomFieldType.TaskTitleSelect)
            return normalized;

        var sourceTasks = (field.SourceTaskList?.Tasks ?? [])
            .Where(task => task.DeletedAt is null)
            .ToList();

        if (Guid.TryParse(normalized, out var taskId)
            && sourceTasks.Any(task => task.Id == taskId))
            return CustomFieldSelectOptions.TaskValue(taskId);

        var legacyMatch = sourceTasks.FirstOrDefault(task =>
            string.Equals((task.Title ?? "").Trim(), normalized, StringComparison.OrdinalIgnoreCase));

        if (legacyMatch is not null)
            return CustomFieldSelectOptions.TaskValue(legacyMatch.Id);

        throw new ArgumentException($"Das benutzerdefinierte Feld „{field.Name}“ verweist auf keine vorhandene Aufgabe.");
    }

    private static void ApplyCustomFieldValues(TodoTaskEntity entity, IReadOnlyList<NormalizedCustomFieldValue> incoming)
    {
        entity.CustomFieldValues ??= new List<TodoTaskCustomFieldValueEntity>();

        foreach (var value in incoming)
        {
            entity.CustomFieldValues.Add(new TodoTaskCustomFieldValueEntity
            {
                Id = Guid.NewGuid(),
                TaskId = entity.Id,
                FieldId = value.FieldId,
                Value = value.Value
            });
        }
    }

    private static async Task ApplyCustomFieldValuesAsync(
        ApplicationDbContext db,
        Guid taskId,
        IReadOnlyList<NormalizedCustomFieldValue> incoming,
        CancellationToken ct)
    {
        var existingValues = await db.TodoTaskCustomFieldValues
            .Where(v => v.TaskId == taskId)
            .ToListAsync(ct);

        var incomingFieldIds = incoming.Select(v => v.FieldId).ToHashSet();

        foreach (var existing in existingValues.Where(v => !incomingFieldIds.Contains(v.FieldId)))
            db.TodoTaskCustomFieldValues.Remove(existing);

        foreach (var value in incoming)
        {
            var existing = existingValues.FirstOrDefault(v => v.FieldId == value.FieldId);
            if (existing is not null)
            {
                existing.Value = value.Value;
                continue;
            }

            db.TodoTaskCustomFieldValues.Add(new TodoTaskCustomFieldValueEntity
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                FieldId = value.FieldId,
                Value = value.Value
            });
        }
    }

    private sealed record NormalizedCustomFieldValue(Guid FieldId, string Value);

    private static TodoTaskEntity SnapshotForAutomation(TodoTaskEntity task)
        => new()
        {
            Id = task.Id,
            ListId = task.ListId,
            Title = task.Title,
            Description = task.Description,
            Column = task.Column,
            Done = task.Done,
            IsImportant = task.IsImportant,
            Assignee = task.Assignee,
            ApproverUserId = task.ApproverUserId,
            ApprovalStatus = task.ApprovalStatus,
            ApprovalRequestedAtUtc = task.ApprovalRequestedAtUtc,
            ApprovalRequestedByUserId = task.ApprovalRequestedByUserId,
            ApprovalDecisionAtUtc = task.ApprovalDecisionAtUtc,
            ApprovalDecisionByUserId = task.ApprovalDecisionByUserId,
            StartDate = task.StartDate,
            DueDate = task.DueDate,
            CardColor = task.CardColor,
            CardColorMode = task.CardColorMode,
            CustomFieldValues = (task.CustomFieldValues ?? [])
                .Select(x => new TodoTaskCustomFieldValueEntity
                {
                    Id = x.Id,
                    TaskId = x.TaskId,
                    FieldId = x.FieldId,
                    Value = x.Value
                })
                .ToList()
        };

    /// <inheritdoc />
    public async Task ReorderKanbanColumnAsync(string userId, Guid listId, string column, IReadOnlyList<Guid> orderedTaskIds, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            return;

        if (!CanWrite(userId, list))
            throw new UnauthorizedAccessException($"Reihenfolge (Kanban) kann nicht gespeichert werden (Liste='{list.Name}', User='{userId}').");

        var tasksInColumn = await db.TodoTasks
            // Kanban-Done-Spalten enthalten absichtlich Done=true-Aufgaben und sind
            // genauso frei sortierbar wie aktive Spalten. Der frühere !t.Done-Filter
            // ließ ihre Reihenfolge nach einem Reload unverändert erscheinen.
            .Where(t => t.ListId == listId && t.DeletedAt == null && t.Column == column)
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        for (int i = 0; i < orderedTaskIds.Count; i++)
        {
            if (tasksInColumn.TryGetValue(orderedTaskIds[i], out var task))
                task.KanbanSortOrder = i;
        }

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }

    public async Task<bool> SetTaskWatchingAsync(string userId, Guid listId, Guid taskId, bool watching, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null || !CanRead(userId, list))
            return false;

        var taskExists = await db.TodoTasks
            .AnyAsync(t => t.Id == taskId && t.ListId == listId && t.DeletedAt == null, cancellationToken);

        if (!taskExists)
            return false;

        var existing = await db.TodoTaskWatchers
            .FirstOrDefaultAsync(w => w.TaskId == taskId && w.UserId == userId, cancellationToken);

        if (watching && existing is null)
            db.TodoTaskWatchers.Add(new TodoTaskWatcherEntity { TaskId = taskId, UserId = userId });
        else if (!watching && existing is not null)
            db.TodoTaskWatchers.Remove(existing);

        await db.SaveChangesAsync(cancellationToken);
        await NotifyTaskUpdatesAsync(listId, taskId, cancellationToken);
        return true;
    }
}
