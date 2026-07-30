using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore;

namespace TodoSuite.Server.Services;

/// <summary>
/// Wendet persönliche Navigationspräferenzen und die für alle Mitglieder verbindliche
/// Portfolio-Zuordnung in einer einheitlichen Reihenfolge auf geladene Listen an.
/// </summary>
internal static class EffectiveNavigationProjection
{
    internal static async Task ApplyAsync(
        ApplicationDbContext db,
        string userId,
        IReadOnlyCollection<TodoListEntity> lists,
        CancellationToken ct)
    {
        if (lists.Count == 0)
            return;

        var listIds = lists.Select(l => l.Id).ToArray();
        var accessibleGroupIds = (await db.TodoListGroups
                .Where(g => g.OwnerId == userId ||
                    (g.IsPortfolio && db.PortfolioParticipants.Any(p =>
                        p.PortfolioGroupId == g.Id && p.UserId == userId && !p.InvitationPending)))
                .Select(g => g.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var preferences = await db.TodoListNavigationPreferences
            .Where(p => p.UserId == userId && listIds.Contains(p.ListId))
            .AsNoTracking()
            .ToListAsync(ct);
        var preferenceMap = preferences
            .GroupBy(p => p.ListId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.UpdatedAtUtc).First());

        foreach (var list in lists)
        {
            if (preferenceMap.TryGetValue(list.Id, out var preference))
            {
                list.NavigationGroupId = preference.NavigationGroupId is Guid groupId && accessibleGroupIds.Contains(groupId)
                    ? groupId
                    : null;
                list.NavigationSortOrder = preference.NavigationSortOrder;
            }
            else
            {
                list.NavigationGroupId = null;
                list.NavigationSortOrder = 0;
            }
        }

        // Die Portfolio-Mitgliedschaft ist fachlich verbindlich und hat Vorrang vor
        // einer persönlichen Präferenz. So erscheinen geerbte Listenfreigaben nicht
        // zusätzlich als einzelne Listen auf der Wurzelebene.
        var memberships = await db.PortfolioLists
            .Where(p => listIds.Contains(p.ListId) && accessibleGroupIds.Contains(p.PortfolioGroupId))
            .AsNoTracking()
            .ToListAsync(ct);
        var membershipMap = memberships.ToDictionary(p => p.ListId);

        // Rückwärtskompatibilität für Portfolios, die bereits vor der kanonischen
        // PortfolioLists-Zuordnung existierten. Deren Listen liegen gegebenenfalls
        // nur in der Navigationspräferenz des Portfolio-Owners. Diese Information
        // gilt fachlich für alle akzeptierten Mitglieder und darf nicht anhand der
        // persönlichen Präferenz des gerade angemeldeten Benutzers verloren gehen.
        var legacyMemberships = await (
            from preference in db.TodoListNavigationPreferences.AsNoTracking()
            join portfolio in db.TodoListGroups.AsNoTracking()
                on preference.NavigationGroupId equals portfolio.Id
            where preference.UserId == portfolio.OwnerId
                  && portfolio.IsPortfolio
                  && accessibleGroupIds.Contains(portfolio.Id)
                  && listIds.Contains(preference.ListId)
            select new
            {
                preference.ListId,
                PortfolioGroupId = portfolio.Id,
                SortOrder = preference.NavigationSortOrder
            })
            .ToListAsync(ct);

        foreach (var list in lists)
        {
            if (membershipMap.TryGetValue(list.Id, out var membership))
            {
                list.NavigationGroupId = membership.PortfolioGroupId;
                list.NavigationSortOrder = membership.SortOrder;
                continue;
            }

            var legacyMembership = legacyMemberships.FirstOrDefault(p => p.ListId == list.Id);
            if (legacyMembership is not null)
            {
                list.NavigationGroupId = legacyMembership.PortfolioGroupId;
                list.NavigationSortOrder = legacyMembership.SortOrder;
            }
        }
    }
}
