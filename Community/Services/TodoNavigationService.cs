using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;

namespace TodoSuite.Server.Services;

public sealed class TodoNavigationService : ITodoNavigationService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IHubContext<TodoHubEndpoint> _hubContext;

    public TodoNavigationService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext)
    {
        _dbContextFactory = dbContextFactory;
        _hubContext = hubContext;
    }

    public async Task<IReadOnlyList<TodoListGroupEntity>> GetListGroupsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var groups = await db.TodoListGroups
            .Where(g => g.OwnerId == userId || db.PortfolioParticipants.Any(p => p.PortfolioGroupId == g.Id && p.UserId == userId && !p.InvitationPending))
            .OrderBy(g => g.SortOrder)
            .AsNoTracking()
            .ToListAsync(ct);

        var groupIds = groups.Select(g => g.Id).ToArray();
        var preferences = await db.TodoListGroupPreferences
            .Where(p => p.UserId == userId && groupIds.Contains(p.GroupId))
            .Select(p => new { p.GroupId, p.IsCollapsed, p.NavigationSortOrder })
            .ToListAsync(ct);
        var preferencesByGroup = preferences.ToDictionary(p => p.GroupId);
        var administratedIds = (await db.PortfolioParticipants
            .Where(p => groupIds.Contains(p.PortfolioGroupId) && p.UserId == userId &&
                !p.InvitationPending && p.Role == ListRole.Admin)
            .Select(p => p.PortfolioGroupId).ToListAsync(ct)).ToHashSet();
        foreach (var group in groups)
        {
            if (preferencesByGroup.TryGetValue(group.Id, out var preference))
            {
                group.IsCollapsed = preference.IsCollapsed;
                if (preference.NavigationSortOrder.HasValue)
                    group.SortOrder = preference.NavigationSortOrder.Value;
            }
            group.CanManage = string.Equals(group.OwnerId, userId, StringComparison.OrdinalIgnoreCase)
                || administratedIds.Contains(group.Id);
        }
        return groups;
    }

    public async Task<TodoListGroupEntity> AddListGroupAsync(string userId, string name, bool isPortfolio = false, CancellationToken ct = default, Guid? id = null)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException($"Gruppe konnte nicht angelegt werden: Name ist leer. UserId='{userId}'.", nameof(name));

        if (id is { } requestedId && requestedId != Guid.Empty)
        {
            var existing = await db.TodoListGroups.AsNoTracking().FirstOrDefaultAsync(group => group.Id == requestedId, ct);
            if (existing is not null)
            {
                EnsureOwner(userId, existing.OwnerId, $"Gruppe kann nicht erneut angelegt werden (GroupId='{requestedId}').");
                existing.CanManage = true;
                return existing;
            }
        }

        // Globales Maximum über ALLE Navigations-Elemente (Gruppen UND Root-Listen),
        // damit die neue Gruppe sicher ans Ende kommt und kein SortOrder-Konflikt entsteht.
        var maxGroup = await db.TodoListGroups
            .Where(g => g.OwnerId == userId)
            .MaxAsync(g => (int?)g.SortOrder, ct) ?? -1;

        var navLists = await LoadEffectiveNavigationListsAsync(db, userId, ct);
        var maxList = navLists
            .Where(l => l.NavigationGroupId is null)
            .Select(l => (int?)l.NavigationSortOrder)
            .Max() ?? -1;

        var max = Math.Max(maxGroup, maxList);

        var g = new TodoListGroupEntity
        {
            Id = id is { } requestedIdValue && requestedIdValue != Guid.Empty ? requestedIdValue : Guid.NewGuid(),
            OwnerId = userId,
            Name = trimmed,
            IsPortfolio = isPortfolio,
            CanManage = true,
            SortOrder = max + 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        db.TodoListGroups.Add(g);
        await SaveNavigationChangesAsync(db, ct);

        await NotifyListsUpdatedAsync(userId, ct);
        return g;
    }

    public async Task SetListGroupPortfolioAsync(string userId, Guid groupId, bool isPortfolio, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var group = await db.TodoListGroups.FirstOrDefaultAsync(x => x.Id == groupId, ct);
        if (group is null) return;
        EnsureOwner(userId, group.OwnerId, $"Gruppe kann nicht geändert werden (GroupId='{groupId}').");
        if (group.IsPortfolio == isPortfolio) return;
        var affectedUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { userId };
        if (group.IsPortfolio)
            affectedUserIds.UnionWith(await GetPortfolioMemberUserIdsAsync(db, groupId, ct));

        if (isPortfolio)
        {
            var preferences = await db.TodoListNavigationPreferences
                .Where(p => p.UserId == userId && p.NavigationGroupId == groupId)
                .OrderBy(p => p.NavigationSortOrder).ToListAsync(ct);
            var listIds = preferences.Select(p => p.ListId).ToArray();
            var lists = await db.TodoLists.Include(l => l.Participants)
                .Where(l => listIds.Contains(l.Id) && l.DeletedAt == null).ToListAsync(ct);
            var unauthorized = lists.Where(l => !PortfolioAccessCoordinator.CanAdminList(userId, l)).Select(l => l.Name).ToList();
            if (unauthorized.Count > 0)
                throw new UnauthorizedAccessException($"Portfolio kann nicht erstellt werden. Bei folgenden Listen fehlt die Admin-Rolle: {string.Join(", ", unauthorized)}.");

            for (var index = 0; index < preferences.Count; index++)
                db.PortfolioLists.Add(new PortfolioListEntity
                {
                    PortfolioGroupId = groupId, ListId = preferences[index].ListId,
                    SortOrder = index, AddedByUserId = userId
                });
        }
        else
        {
            var memberships = await db.PortfolioLists.Where(p => p.PortfolioGroupId == groupId).ToListAsync(ct);
            var members = await db.PortfolioParticipants.Where(p => p.PortfolioGroupId == groupId).ToListAsync(ct);
            foreach (var membership in memberships)
                foreach (var member in members)
                    await PortfolioAccessCoordinator.RevokePortfolioAccessAsync(db, groupId, membership.ListId, member.UserId, member.Email, ct);
            db.PortfolioLists.RemoveRange(memberships);
            db.PortfolioParticipants.RemoveRange(members);
            db.PortfolioInvites.RemoveRange(await db.PortfolioInvites.Where(i => i.PortfolioGroupId == groupId).ToListAsync(ct));
            db.Dashboards.RemoveRange(await db.Dashboards.Where(x => x.PortfolioGroupId == groupId).ToListAsync(ct));
        }

        group.IsPortfolio = isPortfolio;
        group.UpdatedAtUtc = DateTime.UtcNow;

        await SaveNavigationChangesAsync(db, ct);
        await NotifyListsUpdatedAsync(affectedUserIds, ct);
    }

    public async Task SetListGroupCollapsedAsync(string userId, Guid groupId, bool isCollapsed, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var canSeeGroup = await db.TodoListGroups.AnyAsync(x => x.Id == groupId &&
            (x.OwnerId == userId || db.PortfolioParticipants.Any(p => p.PortfolioGroupId == x.Id && p.UserId == userId && !p.InvitationPending)), ct);
        if (!canSeeGroup)
            throw new UnauthorizedAccessException($"Keine Berechtigung für Gruppe '{groupId}'.");

        var preference = await db.TodoListGroupPreferences
            .FirstOrDefaultAsync(x => x.UserId == userId && x.GroupId == groupId, ct);
        if (preference is null)
        {
            db.TodoListGroupPreferences.Add(new TodoListGroupPreferenceEntity
            {
                UserId = userId,
                GroupId = groupId,
                IsCollapsed = isCollapsed
            });
        }
        else
        {
            preference.IsCollapsed = isCollapsed;
            preference.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameListGroupAsync(string userId, Guid groupId, string newName, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var g = await db.TodoListGroups.FirstOrDefaultAsync(x => x.Id == groupId, ct);
        if (g is null) return;

        if (g.IsPortfolio)
        {
            if (!await PortfolioAccessCoordinator.CanManagePortfolioAsync(db, userId, groupId, ct))
                throw new UnauthorizedAccessException($"Portfolio kann nicht umbenannt werden (GroupId='{groupId}').");
        }
        else
            EnsureOwner(userId, g.OwnerId, $"Gruppe kann nicht umbenannt werden (GroupId='{groupId}').");

        var trimmed = (newName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException($"Gruppe konnte nicht umbenannt werden: Name ist leer. GroupId='{groupId}'.", nameof(newName));

        g.Name = trimmed;
        g.UpdatedAtUtc = DateTime.UtcNow;

        await SaveNavigationChangesAsync(db, ct);
        var affectedUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { userId };
        if (g.IsPortfolio)
            affectedUserIds.UnionWith(await GetPortfolioMemberUserIdsAsync(db, groupId, ct));
        await NotifyListsUpdatedAsync(affectedUserIds, ct);
    }

    public async Task DeleteListGroupAsync(string userId, Guid groupId, bool ungroupLists, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var g = await db.TodoListGroups.FirstOrDefaultAsync(x => x.Id == groupId, ct);
        if (g is null) return;

        EnsureOwner(userId, g.OwnerId, $"Gruppe kann nicht gelöscht werden (GroupId='{groupId}').");
        var affectedUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { userId };

        if (g.IsPortfolio)
        {
            affectedUserIds.UnionWith(await GetPortfolioMemberUserIdsAsync(db, groupId, ct));
            var memberships = await db.PortfolioLists.Where(p => p.PortfolioGroupId == groupId).ToListAsync(ct);
            var members = await db.PortfolioParticipants.Where(p => p.PortfolioGroupId == groupId).ToListAsync(ct);
            foreach (var membership in memberships)
                foreach (var member in members)
                    await PortfolioAccessCoordinator.RevokePortfolioAccessAsync(db, groupId, membership.ListId, member.UserId, member.Email, ct);
        }

        if (ungroupLists)
        {
            var lists = (await LoadEffectiveNavigationListsAsync(db, userId, ct))
                .Where(l => l.NavigationGroupId == groupId)
                .OrderBy(l => l.NavigationSortOrder)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var maxRoot = (await LoadEffectiveNavigationListsAsync(db, userId, ct))
                .Where(l => l.NavigationGroupId == null)
                .Select(l => (int?)l.NavigationSortOrder)
                .Max() ?? -1;

            var maxGroup = await db.TodoListGroups
                .Where(x => x.OwnerId == userId && x.Id != groupId)
                .MaxAsync(x => (int?)x.SortOrder, ct) ?? -1;

            var maxGlobal = Math.Max(maxRoot, maxGroup);

            foreach (var l in lists)
            {
                await SetNavigationPreferenceAsync(db, userId, l, null, ++maxGlobal, ct);
            }
        }

        db.TodoListGroups.Remove(g);
        await SaveNavigationChangesAsync(db, ct);

        await NotifyListsUpdatedAsync(affectedUserIds, ct);
    }

    public async Task ReorderListGroupsAsync(string userId, IReadOnlyList<Guid> orderedGroupIds, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        if (orderedGroupIds is null || orderedGroupIds.Count == 0)
            return;

        var groups = await db.TodoListGroups
            .Where(g => g.OwnerId == userId)
            .ToListAsync(ct);

        var rootLists = (await LoadEffectiveNavigationListsAsync(db, userId, ct))
            .Where(l => l.NavigationGroupId == null)
            .ToList();

        ApplyGlobalRootAndGroupOrder(rootLists, groups, orderedGroupIds: orderedGroupIds);

        foreach (var list in rootLists)
            await SetNavigationPreferenceAsync(db, userId, list, null, list.NavigationSortOrder, ct);

        await SaveNavigationChangesAsync(db, ct);
        await NotifyListsUpdatedAsync(userId, ct);
    }

    public async Task ReorderNavigationListsAsync(string userId, Guid? groupId, IReadOnlyList<Guid> orderedListIds, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var affectedUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { userId };

        if (orderedListIds is null || orderedListIds.Count == 0)
            return;

        if (groupId is null)
        {
            var rootLists = (await LoadEffectiveNavigationListsAsync(db, userId, ct))
                .Where(l => l.NavigationGroupId == null)
                .ToList();

            var groups = await db.TodoListGroups
                .Where(g => g.OwnerId == userId)
                .ToListAsync(ct);

            ApplyGlobalRootAndGroupOrder(rootLists, groups, orderedRootListIds: orderedListIds);

            foreach (var list in rootLists)
                await SetNavigationPreferenceAsync(db, userId, list, null, list.NavigationSortOrder, ct);
        }
        else
        {
            var portfolio = await db.TodoListGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId && g.IsPortfolio, ct);
            if (portfolio is not null)
            {
                if (!await PortfolioAccessCoordinator.CanManagePortfolioAsync(db, userId, portfolio.Id, ct))
                    throw new UnauthorizedAccessException("Nur Portfolio-Admins dürfen die Listenreihenfolge ändern.");
                affectedUserIds.UnionWith(await GetPortfolioMemberUserIdsAsync(db, portfolio.Id, ct));
                var memberships = await db.PortfolioLists.Where(p => p.PortfolioGroupId == portfolio.Id).ToListAsync(ct);
                var order = orderedListIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
                foreach (var membership in memberships)
                {
                    membership.SortOrder = order.TryGetValue(membership.ListId, out var index) ? index : int.MaxValue;
                    membership.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
            else
                await ApplyListOrderAsync(db, userId, groupId, orderedListIds, ct);
        }

        await SaveNavigationChangesAsync(db, ct);
        await NotifyListsUpdatedAsync(affectedUserIds, ct);
    }

    public async Task MoveListAsync(
        string userId,
        Guid listId,
        Guid? fromGroupId,
        Guid? toGroupId,
        IReadOnlyList<Guid> fromOrderedIds,
        IReadOnlyList<Guid> toOrderedIds,
        CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var affectedUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { userId };

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null && !l.IsTemplate, ct);
        if (list is null) return;

        if (!CanRead(userId, list))
            throw new UnauthorizedAccessException($"Liste kann nicht verschoben werden (Liste='{list.Name}', User='{userId}').");

        var currentMembership = await db.PortfolioLists
            .FirstOrDefaultAsync(p => p.ListId == listId, ct);
        if (currentMembership is not null)
            affectedUserIds.UnionWith(await GetPortfolioMemberUserIdsAsync(db, currentMembership.PortfolioGroupId, ct));
        TodoListGroupEntity? targetGroup = null;
        if (toGroupId is Guid targetGroupId)
        {
            targetGroup = await db.TodoListGroups.FirstOrDefaultAsync(g => g.Id == targetGroupId, ct);
            if (targetGroup is null)
                throw new InvalidOperationException($"Zielgruppe nicht gefunden oder keine Berechtigung. GroupId='{toGroupId}'.");
            if (targetGroup.IsPortfolio)
            {
                affectedUserIds.UnionWith(await GetPortfolioMemberUserIdsAsync(db, targetGroupId, ct));
                if (!await PortfolioAccessCoordinator.CanManagePortfolioAsync(db, userId, targetGroupId, ct))
                    throw new UnauthorizedAccessException("Nur Portfolio-Admins dürfen Listen hinzufügen.");
                if (!PortfolioAccessCoordinator.CanAdminList(userId, list))
                    throw new UnauthorizedAccessException("Eine Liste kann nur von einem Listen-Admin zum Portfolio hinzugefügt werden.");
                await PortfolioAccessCoordinator.EnsurePortfolioOwnerIsListAdminAsync(db, targetGroup, list, ct);
            }
            else if (!string.Equals(targetGroup.OwnerId, userId, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Keine Berechtigung für die Zielgruppe.");
        }

        if (currentMembership is not null && currentMembership.PortfolioGroupId != toGroupId)
        {
            if (!await PortfolioAccessCoordinator.CanManagePortfolioAsync(db, userId, currentMembership.PortfolioGroupId, ct))
                throw new UnauthorizedAccessException("Nur Portfolio-Admins dürfen Listen aus dem Portfolio entfernen.");
            await RemovePortfolioMembershipAsync(db, listId, currentMembership.PortfolioGroupId, ct);
            db.PortfolioLists.Remove(currentMembership);
            currentMembership = null;
        }

        if (targetGroup?.IsPortfolio == true)
        {
            if (currentMembership is null)
            {
                currentMembership = new PortfolioListEntity
                {
                    PortfolioGroupId = targetGroup.Id, ListId = listId,
                    SortOrder = int.MaxValue, AddedByUserId = userId
                };
                db.PortfolioLists.Add(currentMembership);
            }
            var members = await db.PortfolioParticipants.Where(p => p.PortfolioGroupId == targetGroup.Id && !p.InvitationPending).ToListAsync(ct);
            foreach (var member in members)
                await PortfolioAccessCoordinator.GrantPortfolioAccessAsync(db, targetGroup.Id, listId, member, currentMembership.SortOrder, ct);
        }

        await SetNavigationPreferenceAsync(db, userId, list, toGroupId, list.NavigationSortOrder, ct);

        // Die Quellgruppe wird anhand der zuletzt gespeicherten Preferences normalisiert.
        // Die verschobene Liste wird danach in der Zielgruppe explizit einsortiert.
        if (currentMembership is null || currentMembership.PortfolioGroupId != fromGroupId)
            await ApplyListOrderAsync(db, userId, fromGroupId, fromOrderedIds, ct);

        {
            var toOrderMap = new Dictionary<Guid, int>();
            for (int i = 0; i < (toOrderedIds?.Count ?? 0); i++)
                toOrderMap[toOrderedIds![i]] = i;

            var targetLists = (await LoadEffectiveNavigationListsAsync(db, userId, ct))
                .Where(l => l.NavigationGroupId == toGroupId && l.Id != listId)
                .ToList();

            foreach (var l in targetLists)
                l.NavigationSortOrder = toOrderMap.TryGetValue(l.Id, out var idx) ? idx : int.MaxValue;

            list.NavigationSortOrder = toOrderMap.TryGetValue(listId, out var movedOrder) ? movedOrder : int.MaxValue;

            var allTarget = targetLists
                .Append(list)
                .OrderBy(x => x.NavigationSortOrder)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < allTarget.Count; i++)
            {
                if (targetGroup?.IsPortfolio == true)
                {
                    var membership = currentMembership is not null && currentMembership.ListId == allTarget[i].Id
                        ? currentMembership
                        : await db.PortfolioLists.FirstAsync(p => p.PortfolioGroupId == targetGroup.Id && p.ListId == allTarget[i].Id, ct);
                    membership.SortOrder = i;
                    membership.UpdatedAtUtc = DateTime.UtcNow;
                }
                await SetNavigationPreferenceAsync(db, userId, allTarget[i], toGroupId, i, ct);
            }
        }

        await SaveNavigationChangesAsync(db, ct);
        await NotifyListsUpdatedAsync(affectedUserIds, ct);
    }

    public async Task ReorderMixedNavigationAsync(string userId, IReadOnlyList<string> orderedDescriptors, CancellationToken ct = default)
    {
        if (orderedDescriptors is null || orderedDescriptors.Count == 0)
            return;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        // Sammle alle Gruppen- und Listen-IDs mit ihren globalen Positionen
        var groupPositions = new Dictionary<Guid, int>();
        var listPositions = new Dictionary<Guid, int>();

        for (int i = 0; i < orderedDescriptors.Count; i++)
        {
            var desc = orderedDescriptors[i] ?? "";
            var colon = desc.IndexOf(':');
            if (colon < 0) continue;

            var type = desc[..colon];
            var idStr = desc[(colon + 1)..];

            if (!Guid.TryParse(idStr, out var id)) continue;

            if (type == "group")
                groupPositions[id] = i;
            else if (type == "list")
                listPositions[id] = i;
        }

        // Gruppenpositionen sind Teil der persönlichen Navigation. Nur beim Eigentümer
        // wird zusätzlich die globale Standardposition für neue Mitglieder aktualisiert.
        if (groupPositions.Count > 0)
        {
            var groups = await db.TodoListGroups
                .Where(g => groupPositions.Keys.Contains(g.Id) &&
                    (g.OwnerId == userId || db.PortfolioParticipants.Any(p =>
                        p.PortfolioGroupId == g.Id && p.UserId == userId && !p.InvitationPending)))
                .ToListAsync(ct);

            foreach (var g in groups)
            {
                if (groupPositions.TryGetValue(g.Id, out var pos))
                {
                    var preference = await db.TodoListGroupPreferences
                        .FirstOrDefaultAsync(p => p.UserId == userId && p.GroupId == g.Id, ct);
                    if (preference is null)
                    {
                        db.TodoListGroupPreferences.Add(new TodoListGroupPreferenceEntity
                        {
                            UserId = userId,
                            GroupId = g.Id,
                            NavigationSortOrder = pos
                        });
                    }
                    else
                    {
                        preference.NavigationSortOrder = pos;
                        preference.UpdatedAtUtc = DateTime.UtcNow;
                    }

                    if (string.Equals(g.OwnerId, userId, StringComparison.OrdinalIgnoreCase))
                    {
                        g.SortOrder = pos;
                        g.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }
            }
        }

        // Root-Listen-NavigationSortOrder auf globale Position setzen
        if (listPositions.Count > 0)
        {
            var lists = (await LoadEffectiveNavigationListsAsync(db, userId, ct))
                .Where(l => l.NavigationGroupId == null && listPositions.Keys.Contains(l.Id))
                .ToList();

            foreach (var l in lists)
            {
                if (listPositions.TryGetValue(l.Id, out var pos))
                    await SetNavigationPreferenceAsync(db, userId, l, null, pos, ct);
            }
        }

        await SaveNavigationChangesAsync(db, ct);
        await NotifyListsUpdatedAsync(userId, ct);
    }

    /* ----------------- helpers ----------------- */

    private static async Task RemovePortfolioMembershipAsync(ApplicationDbContext db, Guid listId, Guid portfolioGroupId, CancellationToken ct)
    {
        var members = await db.PortfolioParticipants.Where(p => p.PortfolioGroupId == portfolioGroupId).ToListAsync(ct);
        foreach (var member in members)
            await PortfolioAccessCoordinator.RevokePortfolioAccessAsync(db, portfolioGroupId, listId, member.UserId, member.Email, ct);
    }

    private static Task<List<string>> GetPortfolioMemberUserIdsAsync(
        ApplicationDbContext db,
        Guid portfolioGroupId,
        CancellationToken ct)
        => db.PortfolioParticipants
            .Where(p => p.PortfolioGroupId == portfolioGroupId && !p.InvitationPending && p.UserId != null)
            .Select(p => p.UserId!)
            .Distinct()
            .ToListAsync(ct);

    private static void EnsureOwner(string userId, string ownerId, string baseMsg)
    {
        if (!string.Equals(ownerId, userId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"{baseMsg} Keine Berechtigung. UserId='{userId}', OwnerId='{ownerId}'.");
    }

    private static async Task ApplyListOrderAsync(
    ApplicationDbContext db,
    string userId,
    Guid? groupId,
    IReadOnlyList<Guid> orderedListIds,
    CancellationToken ct)
    {
        var lists = (await LoadEffectiveNavigationListsAsync(db, userId, ct))
            .Where(l => l.NavigationGroupId == groupId)
            .ToList();

        if (lists.Count == 0) return;

        var orderMap = new Dictionary<Guid, int>();
        if (orderedListIds != null)
        {
            for (int i = 0; i < orderedListIds.Count; i++)
                orderMap[orderedListIds[i]] = i;
        }

        // erst: explizite Reihenfolge
        foreach (var l in lists)
            l.NavigationSortOrder = orderMap.TryGetValue(l.Id, out var idx) ? idx : int.MaxValue;

        // dann: stabil ans Ende durchnummerieren
        var normalized = lists
            .OrderBy(x => x.NavigationSortOrder)
            .ThenBy(x => x.Name)
            .ToList();

        for (int i = 0; i < normalized.Count; i++)
            await SetNavigationPreferenceAsync(db, userId, normalized[i], groupId, i, ct);
    }

    private static async Task<List<TodoListEntity>> LoadEffectiveNavigationListsAsync(
        ApplicationDbContext db,
        string userId,
        CancellationToken ct)
    {
        var lists = await db.TodoLists
            .Include(l => l.Participants)
            .Where(l => l.DeletedAt == null
                        && !l.IsTemplate
                        && (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending && (p.Email == userId || p.UserId == userId))))
            .AsNoTracking()
            .ToListAsync(ct);

        if (lists.Count == 0)
            return lists;

        await EffectiveNavigationProjection.ApplyAsync(db, userId, lists, ct);

        return lists;
    }

    private static async Task SetNavigationPreferenceAsync(
        ApplicationDbContext db,
        string userId,
        TodoListEntity list,
        Guid? groupId,
        int sortOrder,
        CancellationToken ct)
    {
        if (groupId is not null)
        {
            var ownsGroup = await db.TodoListGroups
                .AnyAsync(g => g.Id == groupId && (g.OwnerId == userId || db.PortfolioParticipants.Any(p => p.PortfolioGroupId == g.Id && p.UserId == userId && !p.InvitationPending)), ct);
            if (!ownsGroup)
                groupId = null;
        }

        var pref = db.ChangeTracker.Entries<TodoListNavigationPreferenceEntity>()
            .Where(e => e.State != EntityState.Deleted)
            .Select(e => e.Entity)
            .FirstOrDefault(p => p.UserId == userId && p.ListId == list.Id)
            ?? await db.TodoListNavigationPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId && p.ListId == list.Id, ct);

        if (pref is null)
        {
            db.TodoListNavigationPreferences.Add(new TodoListNavigationPreferenceEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ListId = list.Id,
                NavigationGroupId = groupId,
                NavigationSortOrder = sortOrder,
                UpdatedAtUtc = DateTime.UtcNow
            });
            return;
        }

        pref.NavigationGroupId = groupId;
        pref.NavigationSortOrder = sortOrder;
        pref.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static bool CanRead(string userId, TodoListEntity list)
        => string.Equals(list.OwnerId, userId, StringComparison.OrdinalIgnoreCase)
           || list.Participants.Any(p =>
                !p.InvitationPending
                && (string.Equals(p.UserId, userId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Email, userId, StringComparison.OrdinalIgnoreCase)));

    private static void ApplyGlobalRootAndGroupOrder(
        IReadOnlyList<TodoListEntity> rootLists,
        IReadOnlyList<TodoListGroupEntity> groups,
        IReadOnlyList<Guid>? orderedRootListIds = null,
        IReadOnlyList<Guid>? orderedGroupIds = null)
    {
        if (rootLists.Count == 0 && groups.Count == 0)
            return;

        var rootById = rootLists.ToDictionary(x => x.Id);
        var groupById = groups.ToDictionary(x => x.Id);

        var currentRootIds = rootLists
            .OrderBy(x => x.NavigationSortOrder)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Id);

        var currentGroupIds = groups
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Id);

        var nextRootIds = BuildOrderedIds(orderedRootListIds, rootById, currentRootIds);
        var nextGroupIds = BuildOrderedIds(orderedGroupIds, groupById, currentGroupIds);

        var typePattern = rootLists
            .Select(x => new GlobalNavSlot(false, x.NavigationSortOrder, x.Name))
            .Concat(groups.Select(x => new GlobalNavSlot(true, x.SortOrder, x.Name)))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.IsGroup ? 1 : 0)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.IsGroup)
            .ToList();

        var rootQueue = new Queue<Guid>(nextRootIds);
        var groupQueue = new Queue<Guid>(nextGroupIds);

        for (int i = 0; i < typePattern.Count; i++)
        {
            if (typePattern[i])
            {
                if (!groupQueue.TryDequeue(out var groupId) || !groupById.TryGetValue(groupId, out var group))
                    continue;

                group.SortOrder = i;
                group.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                if (!rootQueue.TryDequeue(out var listId) || !rootById.TryGetValue(listId, out var list))
                    continue;

                list.NavigationSortOrder = i;
            }
        }
    }

    private static List<Guid> BuildOrderedIds<T>(
        IReadOnlyList<Guid>? preferredIds,
        IReadOnlyDictionary<Guid, T> knownItems,
        IEnumerable<Guid> fallbackIds)
    {
        var result = new List<Guid>();
        var seen = new HashSet<Guid>();

        if (preferredIds is not null)
        {
            foreach (var id in preferredIds)
            {
                if (knownItems.ContainsKey(id) && seen.Add(id))
                    result.Add(id);
            }
        }

        foreach (var id in fallbackIds)
        {
            if (knownItems.ContainsKey(id) && seen.Add(id))
                result.Add(id);
        }

        return result;
    }

    private sealed record GlobalNavSlot(bool IsGroup, int SortOrder, string Name);

    private static async Task SaveNavigationChangesAsync(ApplicationDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            var pending = db.ChangeTracker.Entries<TodoListNavigationPreferenceEntity>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .ToList();

            foreach (var entry in db.ChangeTracker.Entries<TodoListNavigationPreferenceEntity>().Where(e => e.State == EntityState.Added))
                entry.State = EntityState.Detached;

            foreach (var added in pending)
            {
                var existing = await db.TodoListNavigationPreferences
                    .FirstOrDefaultAsync(p => p.UserId == added.UserId && p.ListId == added.ListId, ct);

                if (existing is null)
                {
                    db.TodoListNavigationPreferences.Add(added);
                    continue;
                }

                existing.NavigationGroupId = added.NavigationGroupId;
                existing.NavigationSortOrder = added.NavigationSortOrder;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException) as string == "23505";

    private Task NotifyListsUpdatedAsync(string userId, CancellationToken ct)
        => _hubContext.Clients.Group(TodoHub.UserGroup(userId))
            .SendAsync(TodoHub.ListsUpdated, cancellationToken: ct);

    private Task NotifyListsUpdatedAsync(IEnumerable<string> userIds, CancellationToken ct)
        => Task.WhenAll(userIds
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(userId => NotifyListsUpdatedAsync(userId, ct)));
}
