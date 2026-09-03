using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Services;

/// <summary>
/// Searches only workspace objects visible to the current user and projects matching entities
/// into UI-neutral search results.
/// </summary>
public class SearchService : ISearchService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public SearchService(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string userId, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(userId))
            return Array.Empty<SearchResultItem>();

        var q = SearchUtilities.NormalizeQuery(query);
        if (q.Length == 0)
            return [];

        // %, _ und der Escape-Character dürfen nicht als vom Benutzer steuerbare LIKE-Wildcards
        // wirken; Parameterisierung schützt zusätzlich vor SQL-Injection.
        var escapedQuery = EscapeLikePattern(q);
        var pattern = $"%{escapedQuery}%";
        var prefixPattern = $"{escapedQuery}%";

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Die Zugriffsmenge wird vor der Textsuche eingeschränkt. Treffer aus fremden Listen
        // können deshalb weder im Resultat noch über Trefferanzahlen offengelegt werden.
        var accessibleLists = db.TodoLists
            .AsNoTracking()
            .Where(l => l.DeletedAt == null && !l.IsTemplate &&
                        (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending && (p.UserId == userId || p.Email == userId))));

        var listResults = await accessibleLists
            .Where(l => EF.Functions.ILike(l.Name, pattern, "\\"))
            .OrderByDescending(l => EF.Functions.ILike(l.Name, escapedQuery, "\\"))
            .ThenByDescending(l => EF.Functions.ILike(l.Name, prefixPattern, "\\"))
            .ThenBy(l => l.Name)
            .Take(SearchUtilities.MaxResults)
            .Select(l => new SearchResultItem(
                SearchResultKind.List,
                l.Id,
                l.Name,
                null,
                l.Name,
                "name"))
            .ToListAsync(ct);

        var taskResults = await db.TodoTasks
            .AsNoTracking()
            .Where(t => t.DeletedAt == null
                && t.List != null
                && t.List.DeletedAt == null
                && !t.List.IsTemplate
                && (t.List.OwnerId == userId || t.List.Participants.Any(p => !p.InvitationPending && (p.UserId == userId || p.Email == userId))))
            .Where(t => EF.Functions.ILike(t.Title, pattern, "\\")
                || (t.Description != null && EF.Functions.ILike(t.Description, pattern, "\\"))
                || t.Steps.Any(s => EF.Functions.ILike(s.Title, pattern, "\\")))
            .OrderByDescending(t => EF.Functions.ILike(t.Title, escapedQuery, "\\"))
            .ThenByDescending(t => EF.Functions.ILike(t.Title, prefixPattern, "\\"))
            .ThenByDescending(t => EF.Functions.ILike(t.Title, pattern, "\\"))
            .ThenBy(t => t.Title)
            .Take(SearchUtilities.MaxResults)
            .Select(t => new SearchResultItem(
                SearchResultKind.Task,
                t.ListId,
                t.List!.Name,
                t.Id,
                t.Title,
                EF.Functions.ILike(t.Title, pattern, "\\")
                    ? "title"
                    : t.Description != null && EF.Functions.ILike(t.Description, pattern, "\\")
                        ? "description"
                        : "step"))
            .ToListAsync(ct);

        // Die gemeinsame Obergrenze wird ausgewogen auf Listen und Aufgaben verteilt, damit
        // eine Trefferart die andere nicht vollständig aus der Ergebnisliste verdrängt.
        return SearchUtilities.TakeBalanced(listResults, taskResults);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
