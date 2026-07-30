using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore;

namespace TodoSuite.Server.Services;

internal static class PortfolioAccessCoordinator
{
    internal static async Task<IReadOnlyList<PortfolioListEntity>> EnsurePortfolioMembershipsAsync(
        ApplicationDbContext db,
        TodoListGroupEntity portfolio,
        CancellationToken ct)
    {
        var memberships = await db.PortfolioLists
            .Where(p => p.PortfolioGroupId == portfolio.Id)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

        var legacyPreferences = await db.TodoListNavigationPreferences
            .Where(p => p.UserId == portfolio.OwnerId && p.NavigationGroupId == portfolio.Id)
            .OrderBy(p => p.NavigationSortOrder)
            .AsNoTracking()
            .ToListAsync(ct);
        if (legacyPreferences.Count == 0)
            return memberships;

        var legacyListIds = legacyPreferences.Select(p => p.ListId).ToArray();
        var validListIds = (await db.TodoLists
                .Where(l => legacyListIds.Contains(l.Id) && l.DeletedAt == null && !l.IsTemplate)
                .Select(l => l.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var alreadyAssigned = (await db.PortfolioLists
                .Where(p => legacyListIds.Contains(p.ListId))
                .Select(p => p.ListId)
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var preference in legacyPreferences.Where(p =>
                     validListIds.Contains(p.ListId) && !alreadyAssigned.Contains(p.ListId)))
        {
            var membership = new PortfolioListEntity
            {
                PortfolioGroupId = portfolio.Id,
                ListId = preference.ListId,
                SortOrder = preference.NavigationSortOrder,
                AddedByUserId = portfolio.OwnerId
            };
            memberships.Add(membership);
            db.PortfolioLists.Add(membership);
        }

        if (memberships.Count > 0)
            await db.SaveChangesAsync(ct);

        return memberships.OrderBy(p => p.SortOrder).ToList();
    }

    internal static async Task EnsureUserPortfolioAccessAsync(
        ApplicationDbContext db,
        string userId,
        CancellationToken ct)
    {
        var participants = await db.PortfolioParticipants
            .Where(p => p.UserId == userId && !p.InvitationPending)
            .ToListAsync(ct);
        if (participants.Count == 0)
            return;

        var portfolioIds = participants.Select(p => p.PortfolioGroupId).Distinct().ToArray();
        var portfolios = await db.TodoListGroups
            .Where(g => portfolioIds.Contains(g.Id) && g.IsPortfolio)
            .ToDictionaryAsync(g => g.Id, ct);

        foreach (var participant in participants)
        {
            if (!portfolios.TryGetValue(participant.PortfolioGroupId, out var portfolio))
                continue;

            var memberships = await EnsurePortfolioMembershipsAsync(db, portfolio, ct);
            foreach (var membership in memberships)
                await GrantPortfolioAccessAsync(
                    db,
                    portfolio.Id,
                    membership.ListId,
                    participant,
                    membership.SortOrder,
                    ct);
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }

    internal static void NormalizeLegacyAccess(ListParticipantEntity participant)
    {
        if (participant.DirectRole is null && participant.PortfolioRole is null && participant.DirectoryRole is null)
        {
            participant.DirectRole = participant.Role;
            participant.DirectInvitationPending = participant.InvitationPending;
        }
    }

    internal static void SetDirectAccess(ListParticipantEntity participant, ListRole role, bool invitationPending)
    {
        NormalizeLegacyAccess(participant);
        participant.DirectRole = role;
        participant.DirectInvitationPending = invitationPending;
        participant.RecalculateEffectiveAccess();
    }

    internal static async Task GrantPortfolioAccessAsync(
        ApplicationDbContext db,
        Guid portfolioGroupId,
        Guid listId,
        PortfolioParticipantEntity member,
        int sortOrder,
        CancellationToken ct)
    {
        if (member.InvitationPending || string.IsNullOrWhiteSpace(member.UserId))
            return;

        var email = member.Email.Trim();
        var participant = await db.ListParticipants.FirstOrDefaultAsync(p =>
            p.ListId == listId &&
            (p.UserId == member.UserId || (!string.IsNullOrWhiteSpace(email) && p.Email.ToLower() == email.ToLower())), ct);

        if (participant is null)
        {
            participant = new ListParticipantEntity
            {
                ListId = listId,
                UserId = member.UserId,
                Email = email,
                DisplayName = member.DisplayName,
                PortfolioRole = member.Role,
                SourcePortfolioGroupId = portfolioGroupId
            };
            participant.RecalculateEffectiveAccess();
            db.ListParticipants.Add(participant);
        }
        else
        {
            NormalizeLegacyAccess(participant);
            participant.UserId ??= member.UserId;
            if (string.IsNullOrWhiteSpace(participant.Email)) participant.Email = email;
            if (string.IsNullOrWhiteSpace(participant.DisplayName)) participant.DisplayName = member.DisplayName;
            participant.PortfolioRole = member.Role;
            participant.SourcePortfolioGroupId = portfolioGroupId;
            participant.RecalculateEffectiveAccess();
        }

        var preference = db.ChangeTracker.Entries<TodoListNavigationPreferenceEntity>()
            .Where(e => e.State != EntityState.Deleted)
            .Select(e => e.Entity)
            .FirstOrDefault(p => p.UserId == member.UserId && p.ListId == listId)
            ?? await db.TodoListNavigationPreferences
                .FirstOrDefaultAsync(p => p.UserId == member.UserId && p.ListId == listId, ct);
        if (preference is null)
        {
            db.TodoListNavigationPreferences.Add(new TodoListNavigationPreferenceEntity
            {
                UserId = member.UserId,
                ListId = listId,
                NavigationGroupId = portfolioGroupId,
                NavigationSortOrder = sortOrder
            });
        }
        else
        {
            preference.NavigationGroupId = portfolioGroupId;
            preference.NavigationSortOrder = sortOrder;
            preference.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    internal static async Task RevokePortfolioAccessAsync(
        ApplicationDbContext db,
        Guid portfolioGroupId,
        Guid listId,
        string? userId,
        string? email,
        CancellationToken ct)
    {
        var normalizedEmail = (email ?? string.Empty).Trim();
        var participants = await db.ListParticipants.Where(p =>
            p.ListId == listId && p.SourcePortfolioGroupId == portfolioGroupId &&
            ((!string.IsNullOrWhiteSpace(userId) && p.UserId == userId) ||
             (!string.IsNullOrWhiteSpace(normalizedEmail) && p.Email.ToLower() == normalizedEmail.ToLower())))
            .ToListAsync(ct);

        foreach (var participant in participants)
        {
            NormalizeLegacyAccess(participant);
            participant.PortfolioRole = null;
            participant.SourcePortfolioGroupId = null;
            if (participant.DirectRole is null)
                db.ListParticipants.Remove(participant);
            else
                participant.RecalculateEffectiveAccess();
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            var preference = await db.TodoListNavigationPreferences.FirstOrDefaultAsync(p =>
                p.UserId == userId && p.ListId == listId && p.NavigationGroupId == portfolioGroupId, ct);
            if (preference is not null)
            {
                preference.NavigationGroupId = null;
                preference.UpdatedAtUtc = DateTime.UtcNow;
            }
        }
    }

    internal static Task<bool> CanManagePortfolioAsync(
        ApplicationDbContext db, string userId, Guid groupId, CancellationToken ct)
        => db.TodoListGroups.AnyAsync(g => g.Id == groupId && g.IsPortfolio &&
            (g.OwnerId == userId || db.PortfolioParticipants.Any(p => p.PortfolioGroupId == g.Id &&
                p.UserId == userId && !p.InvitationPending && p.Role == ListRole.Admin)), ct);

    internal static bool CanAdminList(string userId, TodoListEntity list)
        => string.Equals(list.OwnerId, userId, StringComparison.OrdinalIgnoreCase)
           || list.Participants.Any(p => !p.InvitationPending && p.Role == ListRole.Admin &&
               (string.Equals(p.UserId, userId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Email, userId, StringComparison.OrdinalIgnoreCase)));

    internal static async Task EnsurePortfolioOwnerIsListAdminAsync(
        ApplicationDbContext db, TodoListGroupEntity portfolio, TodoListEntity list, CancellationToken ct)
    {
        if (string.Equals(list.OwnerId, portfolio.OwnerId, StringComparison.OrdinalIgnoreCase)) return;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == portfolio.OwnerId, ct);
        var email = user?.Email ?? string.Empty;
        var participant = list.Participants.FirstOrDefault(p => p.UserId == portfolio.OwnerId ||
            (!string.IsNullOrWhiteSpace(email) && p.Email.ToLower() == email.ToLower()));
        if (participant is null)
        {
            participant = new ListParticipantEntity
            {
                ListId = list.Id, UserId = portfolio.OwnerId, Email = email,
                DisplayName = user?.UserName ?? email, DirectRole = ListRole.Admin,
                Role = ListRole.Admin, DirectInvitationPending = false, InvitationPending = false
            };
            list.Participants.Add(participant);
        }
        else
        {
            participant.UserId = portfolio.OwnerId;
            if (string.IsNullOrWhiteSpace(participant.Email)) participant.Email = email;
            SetDirectAccess(participant, ListRole.Admin, invitationPending: false);
        }
    }
}
