using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;
using Klassenbibliothek.Features;

namespace TodoSuite.Server.Services;

/// <summary>
/// Implements list creation, retrieval, updates, templates, and ownership-sensitive deletion.
/// All returned lists are filtered through the caller's effective workspace permissions.
/// </summary>
public class TodoListService : TodoWorkspaceServiceBase, ITodoListService
{
    private const int MaxListNameLength = 200;
    private readonly IProductFeatureCatalog? _features;
    private bool CustomFieldsEnabled => _features?.IsEnabled(ProductFeatureIds.Forms) ?? true;
    /// <summary>
    /// Erstellt eine neue Instanz der Listenverwaltung.
    /// </summary>
    public TodoListService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService,
        IProductFeatureCatalog? features = null)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
        _features = features;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoListEntity>> GetListsAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        await PortfolioAccessCoordinator.EnsureUserPortfolioAccessAsync(db, userId, cancellationToken);

        var lists = await db.TodoLists
            .Include(l => l.Tasks.Where(t => t.DeletedAt == null)).ThenInclude(t => t.Attachments)
            .Include(l => l.Tasks.Where(t => t.DeletedAt == null)).ThenInclude(t => t.Comments)
            .Include(l => l.Tasks.Where(t => t.DeletedAt == null)).ThenInclude(t => t.Steps)
            .Include(l => l.Tasks.Where(t => t.DeletedAt == null)).ThenInclude(t => t.LabelLinks).ThenInclude(x => x.Label)
            .Include(l => l.Tasks.Where(t => t.DeletedAt == null)).ThenInclude(t => t.Members)
            .Include(l => l.Tasks.Where(t => t.DeletedAt == null)).ThenInclude(t => t.Watchers)
            .Include(l => l.Tasks.Where(t => t.DeletedAt == null)).ThenInclude(t => t.CustomFieldValues)
            .Include(l => l.Labels)
            .Include(l => l.CustomFields).ThenInclude(f => f.Options)
            .Include(l => l.Participants)
            .Include(l => l.Watchers)
            // Die Sichtbarkeitsprüfung ist Bestandteil der Datenbankabfrage. Dadurch werden
            // fremde Listen samt Unterobjekten gar nicht erst in den Prozess geladen.
            .Where(l => l.DeletedAt == null && !l.IsTemplate && (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending && (p.Email == userId || p.UserId == userId))))
            .OrderBy(l => l.Name)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var l in lists)
        {
            // Soft-gelöschte Aufgaben aus der In-Memory-Liste ausblenden
            l.Tasks = (l.Tasks ?? new List<TodoTaskEntity>())
                .Where(t => t.DeletedAt == null)
                .ToList();

            foreach (var t in l.Tasks)
            {
                t.MemberUserIds = (t.Members ?? new List<TodoTaskMemberEntity>())
                    .Where(m => !string.IsNullOrWhiteSpace(m.UserId))
                    .Select(m => m.UserId.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        var listIds = lists.Select(l => l.Id).ToArray();
        var prefs = await db.ListViewPreferences
            .Where(p => p.UserId == userId && listIds.Contains(p.ListId))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var prefMap = prefs.ToDictionary(p => p.ListId);

        foreach (var l in lists)
        {
            if (prefMap.TryGetValue(l.Id, out var pref))
                ApplyPreferenceToList(l, pref);
        }

        await ApplyNavigationPreferencesAsync(db, userId, lists, cancellationToken);

        return lists
            .OrderBy(l => l.NavigationGroupId.HasValue)
            .ThenBy(l => l.NavigationGroupId)
            .ThenBy(l => l.NavigationSortOrder)
            .ThenBy(l => l.Name)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<TodoListEntity?> GetListAsync(string userId, Guid listId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await LoadFullListAsync(db, userId, listId, cancellationToken);
        if (list is null)
            return null;

        await ApplyListPreferencesAsync(db, userId, [list], cancellationToken);
        return list;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoListEntity>> GetNavigationListsAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        await PortfolioAccessCoordinator.EnsureUserPortfolioAccessAsync(db, userId, cancellationToken);

        var lists = await db.TodoLists
            .Where(l => l.DeletedAt == null
                && !l.IsTemplate
                && (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending && (p.Email == userId || p.UserId == userId))))
            .OrderBy(l => l.Name)
            .Select(l => new TodoListEntity
            {
                Id = l.Id,
                OwnerId = l.OwnerId,
                Name = l.Name,
                DefaultView = l.DefaultView,
                BackgroundColor = l.BackgroundColor
            })
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        await ApplyNavigationPreferencesAsync(db, userId, lists, cancellationToken);

        return lists
            .OrderBy(l => l.NavigationGroupId.HasValue)
            .ThenBy(l => l.NavigationGroupId)
            .ThenBy(l => l.NavigationSortOrder)
            .ThenBy(l => l.Name)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoListEntity>> GetListOptionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        return await LoadListOptionsAsync(db, userId, sourceListIds: Array.Empty<Guid>(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoListEntity>> GetWorkspaceListsAsync(string userId, Guid? currentListId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var lists = (await LoadListOptionsAsync(db, userId, sourceListIds: Array.Empty<Guid>(), cancellationToken)).ToList();
        if (lists.Count == 0)
            return lists;

        var selectedListId = currentListId.HasValue && lists.Any(l => l.Id == currentListId.Value)
            ? currentListId.Value
            : lists[0].Id;

        var currentList = await LoadFullListAsync(db, userId, selectedListId, cancellationToken);
        if (currentList is null)
            return lists;

        var sourceListIds = (currentList.CustomFields ?? [])
            .Where(f => f.Type == TodoCustomFieldType.TaskTitleSelect && f.SourceTaskListId.HasValue)
            .Select(f => f.SourceTaskListId!.Value)
            .Where(id => id != currentList.Id)
            .Distinct()
            .ToArray();

        if (sourceListIds.Length > 0)
            lists = (await LoadListOptionsAsync(db, userId, sourceListIds, cancellationToken)).ToList();

        await ApplyListPreferencesAsync(db, userId, [currentList], cancellationToken);

        var selectedIndex = lists.FindIndex(l => l.Id == currentList.Id);
        if (selectedIndex >= 0)
            lists[selectedIndex] = currentList;
        else
            lists.Insert(0, currentList);

        return lists;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoListEntity>> GetAssignedTaskListsAsync(
        string userId,
        IReadOnlyCollection<string> assigneeKeys,
        CancellationToken cancellationToken = default)
    {
        var normalizedKeys = (assigneeKeys ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedKeys.Count == 0)
            return Array.Empty<TodoListEntity>();

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var lists = await db.TodoLists
            .Include(l => l.Labels)
            .Include(l => l.CustomFields).ThenInclude(f => f.Options)
            .Include(l => l.Participants)
            .Where(l => l.DeletedAt == null
                && !l.IsTemplate
                && (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending && (p.Email == userId || p.UserId == userId))))
            .OrderBy(l => l.Name)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var listIds = lists.Select(l => l.Id).ToArray();
        if (listIds.Length == 0)
            return lists;

        normalizedKeys = AssigneeIdentityKeys.ExpandWithAcceptedParticipants(normalizedKeys, lists);

        var loweredKeys = normalizedKeys
            .Select(k => k.ToLowerInvariant())
            .ToArray();

        var candidateTasks = await db.TodoTasks
            .Include(t => t.Attachments)
            .Include(t => t.Comments)
            .Include(t => t.Steps)
            .Include(t => t.LabelLinks).ThenInclude(x => x.Label)
            .Include(t => t.Members)
            .Include(t => t.Watchers)
            .Include(t => t.CustomFieldValues)
            .Where(t => listIds.Contains(t.ListId)
                && t.DeletedAt == null
                && t.Assignee != null
                && t.Assignee != ""
                && loweredKeys.Contains(t.Assignee.Trim().ToLower()))
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var assignedTasks = candidateTasks
            .Where(t => !string.IsNullOrWhiteSpace(t.Assignee) && normalizedKeys.Contains(t.Assignee.Trim()))
            .ToList();

        foreach (var t in assignedTasks)
        {
            t.MemberUserIds = (t.Members ?? new List<TodoTaskMemberEntity>())
                .Where(m => !string.IsNullOrWhiteSpace(m.UserId))
                .Select(m => m.UserId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var tasksByList = assignedTasks
            .GroupBy(t => t.ListId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var list in lists)
            list.Tasks = tasksByList.TryGetValue(list.Id, out var tasks) ? tasks : new List<TodoTaskEntity>();

        return lists;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoListEntity>> GetTemplatesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.TodoLists
            .Include(l => l.Labels)
            .Include(l => l.CustomFields).ThenInclude(f => f.Options)
            .Where(l => l.DeletedAt == null && l.IsTemplate && l.OwnerId == userId)
            .OrderBy(l => l.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private static async Task ApplyNavigationPreferencesAsync(
        ApplicationDbContext db,
        string userId,
        IReadOnlyCollection<TodoListEntity> lists,
        CancellationToken cancellationToken)
        => await EffectiveNavigationProjection.ApplyAsync(db, userId, lists, cancellationToken);

    /// <inheritdoc />
    public async Task<TodoListEntity> CreateListFromTemplateAsync(string userId, Guid templateId, string newName, CancellationToken cancellationToken = default)
    {
        var trimmed = NormalizeListName(newName, nameof(newName));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var template = await db.TodoLists
            .Include(l => l.Labels)
            .Include(l => l.CustomFields).ThenInclude(f => f.Options)
            .Include(l => l.CustomFields).ThenInclude(f => f.SourceTaskList)!.ThenInclude(l => l!.Participants)
            // Vorlagen sind privat: Die Kenntnis einer Template-ID genügt nicht als Zugriffsnachweis.
            .FirstOrDefaultAsync(l => l.Id == templateId && l.IsTemplate && l.OwnerId == userId && l.DeletedAt == null, cancellationToken);

        if (template is null)
            throw new InvalidOperationException($"Vorlage nicht gefunden oder kein Zugriff. TemplateId='{templateId}'.");

        var newListId = Guid.NewGuid();
        var columns = NormalizeColumns(template.Columns);
        var newList = new TodoListEntity
        {
            Id = newListId,
            OwnerId = userId,
            Name = trimmed,
            IsTemplate = false,
            Columns = columns,
            DoneColumns = NormalizeDoneColumns(template.DoneColumns, columns),
            DefaultView = template.DefaultView,
            BackgroundColor = template.BackgroundColor,
            Tasks = new List<TodoTaskEntity>(),
            Participants = new List<ListParticipantEntity>(),
            Labels = (template.Labels ?? new List<TodoLabelEntity>())
                .Select(l => new TodoLabelEntity
                {
                    Id = Guid.NewGuid(),
                    Title = l.Title,
                    BackgroundColor = l.BackgroundColor
                })
                .ToList(),
            CustomFields = (CustomFieldsEnabled ? template.CustomFields : [])
                .OrderBy(f => f.SortOrder)
                .Select((f, index) => new TodoCustomFieldDefinitionEntity
                {
                    Id = Guid.NewGuid(),
                    Name = f.Name,
                    Type = f.Type,
                    IsRequired = f.IsRequired,
                    // Fremde Quelllisten werden nur übernommen, wenn der Ersteller sie administrieren
                    // darf; ein Template darf keine versteckte Referenz als Berechtigungsumgehung kopieren.
                    SourceTaskListId = f.Type == TodoCustomFieldType.TaskTitleSelect
                        && f.SourceTaskListId == template.Id
                            ? newListId
                            : f.Type == TodoCustomFieldType.TaskTitleSelect
                              && f.SourceTaskList is not null
                              && CanAdmin(userId, f.SourceTaskList)
                                ? f.SourceTaskListId
                            : null,
                    SortOrder = index,
                    Options = (f.Options ?? new List<TodoCustomFieldOptionEntity>())
                        .OrderBy(o => o.SortOrder)
                        .Select((o, optionIndex) => new TodoCustomFieldOptionEntity
                        {
                            Id = Guid.NewGuid(),
                            Value = o.Value,
                            SortOrder = optionIndex
                        })
                        .ToList()
                })
                .ToList()
        };

        newList.NavigationGroupId = null;
        newList.NavigationSortOrder = await GetNextNavigationSortOrderAsync(db, userId, groupId: null, cancellationToken);

        await EnsureOwnerParticipantAdminAsync(db, newList, userId, cancellationToken);

        db.TodoLists.Add(newList);
        db.TodoListNavigationPreferences.Add(new TodoListNavigationPreferenceEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ListId = newList.Id,
            NavigationGroupId = newList.NavigationGroupId,
            NavigationSortOrder = newList.NavigationSortOrder,
            UpdatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(newList.Id, cancellationToken);
        await NotifyParticipantsListsUpdatedAsync(newList, cancellationToken);

        return newList;
    }

    /// <inheritdoc />
    public async Task<TodoListEntity> AddListAsync(string userId, TodoListEntity list, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        list.Name = NormalizeListName(list.Name, nameof(list));
        list.Columns = NormalizeColumns(list.Columns);
        list.DoneColumns = NormalizeDoneColumns(list.DoneColumns, list.Columns);
        var background = (list.BackgroundColor ?? string.Empty).Trim();
        list.BackgroundColor = IsValidHexColor(background) ? background.ToUpperInvariant() : null;
        if (list.IsTemplate)
            list.Tasks = [];

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        list.Id = list.Id == Guid.Empty ? Guid.NewGuid() : list.Id;

        var existing = await db.TodoLists
            .Include(l => l.Tasks)
            .Include(l => l.Labels)
            .Include(l => l.CustomFields).ThenInclude(f => f.Options)
            .Include(l => l.Participants)
            .Include(l => l.Watchers)
            .FirstOrDefaultAsync(l => l.Id == list.Id && l.DeletedAt == null, cancellationToken);

        if (existing is not null)
        {
            // Mobile Wiederholungen dürfen dieselbe clientseitig erzeugte ID idempotent verwenden,
            // aber nur wenn die bereits vorhandene Liste für diesen Benutzer lesbar ist.
            if (!CanRead(userId, existing))
                throw new UnauthorizedAccessException($"Liste '{existing.Name}' kann nicht erneut angelegt werden (User='{userId}').");

            return existing;
        }

        list.OwnerId = userId;
        if (!CustomFieldsEnabled)
        {
            list.CustomFields = [];
            foreach (var task in list.Tasks ?? [])
                task.CustomFieldValues = [];
        }

        list.Columns ??= new List<string> { "Backlog", "In Arbeit", "Erledigt" };
        list.Tasks ??= new List<TodoTaskEntity>();
        list.Participants ??= new List<ListParticipantEntity>();

        var requestedGroupId = await ResolveNavigationGroupIdAsync(db, userId, list.NavigationGroupId, cancellationToken);
        list.NavigationGroupId = requestedGroupId;
        list.NavigationSortOrder = await GetNextNavigationSortOrderAsync(db, userId, requestedGroupId, cancellationToken);

        await EnsureOwnerParticipantAdminAsync(db, list, userId, cancellationToken);

        foreach (var p in list.Participants.ToList())
        {
            var isOwner = EqualsUserKey(p.Email, userId) || EqualsUserKey(p.UserId, userId);
            if (!isOwner)
            {
                p.Id = p.Id == Guid.Empty ? Guid.NewGuid() : p.Id;
                p.ListId = list.Id;
                p.Role = ListRole.Member;
                p.InvitationPending = true;
                p.DirectRole = ListRole.Member;
                p.DirectInvitationPending = true;
            }
            else
            {
                p.ListId = list.Id;
            }
        }

        db.TodoLists.Add(list);
        db.TodoListNavigationPreferences.Add(new TodoListNavigationPreferenceEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ListId = list.Id,
            NavigationGroupId = list.NavigationGroupId,
            NavigationSortOrder = list.NavigationSortOrder,
            UpdatedAtUtc = DateTime.UtcNow
        });
        if (list.NavigationGroupId is Guid portfolioGroupId &&
            await db.TodoListGroups.FirstOrDefaultAsync(g => g.Id == portfolioGroupId && g.IsPortfolio, cancellationToken) is { } portfolio)
        {
            if (!await PortfolioAccessCoordinator.CanManagePortfolioAsync(db, userId, portfolioGroupId, cancellationToken))
                throw new UnauthorizedAccessException("Nur Portfolio-Admins dürfen direkt in diesem Portfolio Listen anlegen.");
            await PortfolioAccessCoordinator.EnsurePortfolioOwnerIsListAdminAsync(db, portfolio, list, cancellationToken);
            var sortOrder = (await db.PortfolioLists.Where(p => p.PortfolioGroupId == portfolioGroupId)
                .MaxAsync(p => (int?)p.SortOrder, cancellationToken) ?? -1) + 1;
            db.PortfolioLists.Add(new PortfolioListEntity
            {
                PortfolioGroupId = portfolioGroupId, ListId = list.Id,
                SortOrder = sortOrder, AddedByUserId = userId
            });
            var members = await db.PortfolioParticipants
                .Where(p => p.PortfolioGroupId == portfolioGroupId && !p.InvitationPending).ToListAsync(cancellationToken);
            foreach (var member in members)
                await PortfolioAccessCoordinator.GrantPortfolioAccessAsync(db, portfolioGroupId, list.Id, member, sortOrder, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(list.Id, cancellationToken);
        await NotifyParticipantsListsUpdatedAsync(list, cancellationToken);

        return list;
    }

    /// <inheritdoc />
    public async Task<TodoListEntity?> UpdateListAsync(string userId, TodoListEntity list, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.Tasks)
            .FirstOrDefaultAsync(l => l.Id == list.Id, cancellationToken);

        if (entity is null)
            return null;

        if (!CanAdmin(userId, entity))
            throw new UnauthorizedAccessException($"Liste '{entity.Name}' kann nicht geändert werden (User='{userId}').");

        if (list.SyncVersion.HasValue && list.SyncVersion.Value != entity.ContentVersion)
            throw new WorkspaceConcurrencyException("Die Liste wurde zwischenzeitlich auf einem anderen Gerät geändert.");
        entity.ContentVersion++;

        entity.Name = NormalizeListName(list.Name, nameof(list));

        entity.DefaultView = list.DefaultView;
        var trimmedBackground = (list.BackgroundColor ?? "").Trim();
        entity.BackgroundColor = IsValidHexColor(trimmedBackground) ? trimmedBackground.ToUpperInvariant() : null;

        var incomingCols = NormalizeColumns(list.Columns);

        entity.Columns = incomingCols;
        entity.DoneColumns = NormalizeDoneColumns(list.DoneColumns, incomingCols);

        var incoming = list.Participants ?? new List<ListParticipantEntity>();

        var isOwner = string.Equals(entity.OwnerId, userId, StringComparison.OrdinalIgnoreCase);
        var incomingByKey = incoming
            .Where(p => !string.IsNullOrWhiteSpace(p.Email) || !string.IsNullOrWhiteSpace(p.UserId))
            .GroupBy(p => (p.UserId ?? p.Email)!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(p => (p.UserId ?? p.Email)!.Trim(), StringComparer.OrdinalIgnoreCase);

        var ownerKey = entity.OwnerId;
        var toRemove = entity.Participants
            .Where(p => !EqualsUserKey(p.UserId, ownerKey) && !EqualsUserKey(p.Email, ownerKey))
            .Where(p => !incomingByKey.ContainsKey((p.UserId ?? p.Email)?.Trim() ?? ""))
            .ToList();

        var fullyRemoved = new List<ListParticipantEntity>();
        foreach (var participant in toRemove)
        {
            // Entfernt wird nur die direkte Freigabe. Ein weiterhin gültiger Portfolio-Anteil
            // bleibt erhalten und bestimmt anschließend erneut den effektiven Zugriff.
            PortfolioAccessCoordinator.NormalizeLegacyAccess(participant);
            participant.DirectRole = null;
            participant.DirectInvitationPending = false;
            if (participant.PortfolioRole is null)
            {
                entity.Participants.Remove(participant);
                fullyRemoved.Add(participant);
            }
            else
                participant.RecalculateEffectiveAccess();
        }

        foreach (var inc in incomingByKey.Values)
        {
            var key = (inc.UserId ?? inc.Email)!.Trim();

            if (EqualsUserKey(key, ownerKey))
                continue;

            var existing = entity.Participants.FirstOrDefault(p =>
                EqualsUserKey(p.UserId, key) || EqualsUserKey(p.Email, key));

            if (existing is null)
            {
                entity.Participants.Add(new ListParticipantEntity
                {
                    Id = inc.Id == Guid.Empty ? Guid.NewGuid() : inc.Id,
                    DisplayName = (inc.DisplayName ?? "").Trim(),
                    Email = (inc.Email ?? "").Trim(),
                    UserId = string.IsNullOrWhiteSpace(inc.UserId) ? null : inc.UserId.Trim(),
                    InvitationPending = true,
                    DirectInvitationPending = true,
                    Role = ListRole.Member,
                    DirectRole = ListRole.Member,
                    ListId = entity.Id
                });
            }
            else
            {
                existing.DisplayName = (inc.DisplayName ?? existing.DisplayName).Trim();
                if (!string.IsNullOrWhiteSpace(inc.Email)) existing.Email = inc.Email.Trim();
                if (!string.IsNullOrWhiteSpace(inc.UserId)) existing.UserId = inc.UserId.Trim();

                if (!existing.InvitationPending)
                {
                }
                else
                {
                    existing.InvitationPending = true;
                }

                if (isOwner)
                {
                    PortfolioAccessCoordinator.SetDirectAccess(existing, inc.Role, existing.DirectInvitationPending);
                }
            }
        }

        await EnsureOwnerParticipantAdminAsync(db, entity, entity.OwnerId, cancellationToken);

        var removedUserIds = fullyRemoved
            .Select(p => (p.UserId ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !x.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        db.ListParticipants.RemoveRange(fullyRemoved);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new WorkspaceConcurrencyException("Die Liste wurde gleichzeitig auf einem anderen Gerät geändert.");
        }

        // Aufgabenmitgliedschaften dürfen keinen indirekten Zugriff übrig lassen, nachdem die
        // entsprechende Listenmitgliedschaft vollständig entfernt wurde.
        await TaskMemberService.CleanupRemovedListMembersAsync(entity.Id, removedUserIds, cancellationToken);

        await NotifyListUpdatedAsync(list.Id, cancellationToken);

        entity.SyncVersion = entity.ContentVersion;
        return entity;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteListAsync(string userId, Guid listId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (entity is null)
            return false;

        if (!CanAdmin(userId, entity))
            throw new UnauthorizedAccessException($"Liste '{entity.Name}' kann nicht gelöscht werden (User='{userId}').");

        // Soft-Delete: In Papierkorb verschieben
        entity.DeletedAt = DateTime.UtcNow;
        entity.DeletedByUserId = userId;
        entity.ContentVersion++;

        await db.SaveChangesAsync(cancellationToken);

        // Benachrichtigt alle Teilnehmer über die Löschung, damit deren NavMenu
        // die gelöschte Liste sofort entfernt (NavMenu abonniert nur den User-Gruppe).
        await NotifyParticipantsListsUpdatedAsync(entity, cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task TransferListOwnershipAsync(
        string userId,
        Guid listId,
        string newOwnerUserIdOrEmail,
        CancellationToken cancellationToken = default)
    {
        var target = (newOwnerUserIdOrEmail ?? "").Trim();
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException(
                $"Owner-Transfer fehlgeschlagen: Zielbenutzer ist leer. ListId='{listId}'.",
                nameof(newOwnerUserIdOrEmail));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            throw new InvalidOperationException(
                $"Owner-Transfer fehlgeschlagen: Liste nicht gefunden. ListId='{listId}'.");

        if (!string.Equals(list.OwnerId, userId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Owner-Transfer nicht erlaubt: Nur der Ersteller darf den Owner ändern (Liste='{list.Name}', User='{userId}').");

        var targetParticipant = list.Participants.FirstOrDefault(p =>
            EqualsUserKey(p.Email, target) ||
            (p.UserId is not null && EqualsUserKey(p.UserId, target)));

        if (targetParticipant is null)
            throw new InvalidOperationException(
                $"Owner-Transfer fehlgeschlagen: Zielbenutzer ist kein Teilnehmer der Liste. Liste='{list.Name}', Ziel='{target}'.");

        if (targetParticipant.InvitationPending)
            throw new InvalidOperationException(
                $"Owner-Transfer fehlgeschlagen: Zielbenutzer hat die Einladung noch nicht angenommen. Liste='{list.Name}', Ziel='{targetParticipant.Email}'.");

        await EnsureOwnerParticipantAdminAsync(db, list, list.OwnerId, cancellationToken);

        PortfolioAccessCoordinator.SetDirectAccess(targetParticipant, ListRole.Admin, invitationPending: false);

        if (string.IsNullOrWhiteSpace(targetParticipant.UserId))
        {
            throw new InvalidOperationException(
                $"Owner-Transfer fehlgeschlagen: Zielbenutzer hat keine UserId (Einladung ggf. nicht angenommen). Liste='{list.Name}', Ziel='{targetParticipant.Email}'.");
        }

        list.OwnerId = targetParticipant.UserId;

        await EnsureOwnerParticipantAdminAsync(db, list, list.OwnerId, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RenameListAsync(string userId, Guid listId, string newName, CancellationToken cancellationToken = default)
    {
        var trimmed = NormalizeListName(newName, nameof(newName));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            throw new InvalidOperationException($"Liste konnte nicht umbenannt werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Liste '{list.Name}' kann nicht umbenannt werden (User='{userId}').");

        list.Name = trimmed;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
        await NotifyParticipantsListsUpdatedAsync(list, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetBackgroundColorAsync(string userId, Guid listId, string? backgroundColor, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            throw new InvalidOperationException($"Hintergrundfarbe konnte nicht gesetzt werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Hintergrundfarbe für Liste '{list.Name}' kann nicht geändert werden (User='{userId}').");

        var trimmed = (backgroundColor ?? "").Trim();
        list.BackgroundColor = IsValidHexColor(trimmed) ? trimmed.ToUpperInvariant() : null;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }

    public async Task<bool> SetListWatchingAsync(string userId, Guid listId, bool watching, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null || !CanRead(userId, list))
            return false;

        var existing = await db.TodoListWatchers
            .FirstOrDefaultAsync(w => w.ListId == listId && w.UserId == userId, cancellationToken);

        if (watching && existing is null)
            db.TodoListWatchers.Add(new TodoListWatcherEntity { ListId = listId, UserId = userId });
        else if (!watching && existing is not null)
            db.TodoListWatchers.Remove(existing);

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
        return true;
    }

    private static async Task<TodoListEntity?> LoadFullListAsync(
        ApplicationDbContext db,
        string userId,
        Guid listId,
        CancellationToken cancellationToken)
    {
        var list = await db.TodoLists
            .Include(l => l.Tasks).ThenInclude(t => t.Attachments)
            .Include(l => l.Tasks).ThenInclude(t => t.Comments)
            .Include(l => l.Tasks).ThenInclude(t => t.Steps)
            .Include(l => l.Tasks).ThenInclude(t => t.LabelLinks).ThenInclude(x => x.Label)
            .Include(l => l.Tasks).ThenInclude(t => t.Members)
            .Include(l => l.Tasks).ThenInclude(t => t.Watchers)
            .Include(l => l.Tasks).ThenInclude(t => t.CustomFieldValues)
            .Include(l => l.Labels)
            .Include(l => l.CustomFields).ThenInclude(f => f.Options)
            .Include(l => l.Participants)
            .Include(l => l.Watchers)
            .Where(l => l.Id == listId
                && l.DeletedAt == null
                && !l.IsTemplate
                && (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending && (p.Email == userId || p.UserId == userId))))
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (list is not null)
            NormalizeLoadedTasks([list]);

        return list;
    }

    private static async Task<IReadOnlyList<TodoListEntity>> LoadListOptionsAsync(
        ApplicationDbContext db,
        string userId,
        IReadOnlyCollection<Guid> sourceListIds,
        CancellationToken cancellationToken)
    {
        var lists = await db.TodoLists
            .Include(l => l.Participants)
            .Where(l => l.DeletedAt == null
                && !l.IsTemplate
                && (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending && (p.Email == userId || p.UserId == userId))))
            .OrderBy(l => l.Name)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var list in lists)
        {
            list.Tasks = new List<TodoTaskEntity>();
            list.Labels = new List<TodoLabelEntity>();
            list.CustomFields = new List<TodoCustomFieldDefinitionEntity>();
            list.Watchers = new List<TodoListWatcherEntity>();
        }

        var sourceIds = (sourceListIds ?? Array.Empty<Guid>())
            .Distinct()
            .ToArray();

        if (sourceIds.Length == 0)
            return lists;

        var sourceTasks = await db.TodoTasks
            .Where(t => sourceIds.Contains(t.ListId) && t.DeletedAt == null)
            .Select(t => new TodoTaskEntity
            {
                Id = t.Id,
                ListId = t.ListId,
                Title = t.Title,
                DeletedAt = t.DeletedAt
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var tasksByList = sourceTasks
            .GroupBy(t => t.ListId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var list in lists)
            if (tasksByList.TryGetValue(list.Id, out var tasks))
                list.Tasks = tasks;

        return lists;
    }

    private static void NormalizeLoadedTasks(IEnumerable<TodoListEntity> lists)
    {
        foreach (var l in lists)
        {
            l.Tasks = (l.Tasks ?? new List<TodoTaskEntity>())
                .Where(t => t.DeletedAt == null)
                .ToList();

            foreach (var t in l.Tasks)
            {
                t.MemberUserIds = (t.Members ?? new List<TodoTaskMemberEntity>())
                    .Where(m => !string.IsNullOrWhiteSpace(m.UserId))
                    .Select(m => m.UserId.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    private static async Task ApplyListPreferencesAsync(
        ApplicationDbContext db,
        string userId,
        IReadOnlyCollection<TodoListEntity> lists,
        CancellationToken cancellationToken)
    {
        var listIds = lists.Select(l => l.Id).ToArray();
        if (listIds.Length == 0)
            return;

        var prefs = await db.ListViewPreferences
            .Where(p => p.UserId == userId && listIds.Contains(p.ListId))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var prefMap = prefs.ToDictionary(p => p.ListId);

        foreach (var l in lists)
        {
            if (prefMap.TryGetValue(l.Id, out var pref))
                ApplyPreferenceToList(l, pref);
        }
    }

    private static void ApplyPreferenceToList(TodoListEntity list, ListViewPreferenceEntity pref)
    {
        list.DefaultView = pref.LastView;
        if (pref.TableColumnOrder is { Count: > 0 })
            list.TableColumnOrder = pref.TableColumnOrder.ToList();
        if (pref.TableHiddenColumns is { Count: > 0 })
            list.TableHiddenColumns = pref.TableHiddenColumns.ToList();
    }

    private static async Task<int> GetNextNavigationSortOrderAsync(
        ApplicationDbContext db,
        string userId,
        Guid? groupId,
        CancellationToken cancellationToken)
    {
        var maxPreferenceSortOrder = await db.TodoListNavigationPreferences
            .Where(p => p.UserId == userId && p.NavigationGroupId == groupId)
            .MaxAsync(p => (int?)p.NavigationSortOrder, cancellationToken) ?? -1;

        if (groupId is not null)
            return maxPreferenceSortOrder + 1;

        var maxGroupSortOrder = await db.TodoListGroups
            .Where(g => g.OwnerId == userId)
            .MaxAsync(g => (int?)g.SortOrder, cancellationToken) ?? -1;

        return Math.Max(maxPreferenceSortOrder, maxGroupSortOrder) + 1;
    }

    private static async Task<Guid?> ResolveNavigationGroupIdAsync(
        ApplicationDbContext db,
        string userId,
        Guid? groupId,
        CancellationToken cancellationToken)
    {
        if (groupId is null)
            return null;

        var group = await db.TodoListGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == groupId, cancellationToken)
            ?? throw new ArgumentException("Die ausgewählte Gruppe wurde nicht gefunden.", nameof(groupId));

        if (string.Equals(group.OwnerId, userId, StringComparison.OrdinalIgnoreCase))
            return groupId;

        if (group.IsPortfolio &&
            await PortfolioAccessCoordinator.CanManagePortfolioAsync(db, userId, group.Id, cancellationToken))
            return groupId;

        throw new UnauthorizedAccessException("Die Liste darf nicht in der ausgewählten Gruppe erstellt werden.");
    }

    private static bool IsValidHexColor(string? s)
        => !string.IsNullOrWhiteSpace(s)
           && s.Trim().StartsWith("#")
           && s.Trim().Length == 7
           && s.Trim().Skip(1).All(ch => "0123456789abcdefABCDEF".Contains(ch));

    private static string NormalizeListName(string? name, string parameterName)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Listenname darf nicht leer sein.", parameterName);
        if (trimmed.Length > MaxListNameLength)
            throw new ArgumentException($"Ein Listenname darf höchstens {MaxListNameLength} Zeichen lang sein.", parameterName);
        return trimmed;
    }

    private static List<string> NormalizeColumns(IEnumerable<string>? columns)
    {
        var normalized = (columns ?? [])
            .Select(column => (column ?? string.Empty).Trim())
            .Where(column => column.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0 ? ["Backlog", "In Arbeit", "Erledigt"] : normalized;
    }

    private static List<string> NormalizeDoneColumns(IEnumerable<string>? doneColumns, IReadOnlyList<string> columns)
        => (doneColumns ?? [])
            .Select(done => columns.FirstOrDefault(column => string.Equals(column, done?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(done => done is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
