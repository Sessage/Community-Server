using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Services;

public class SearchService : ISearchService
{
    private const int MaxResults = 100;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public SearchService(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string userId, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(userId))
            return Array.Empty<SearchResultItem>();

        var q = query.Trim();
        var pattern = $"%{EscapeLikePattern(q)}%";

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var accessibleLists = db.TodoLists
            .AsNoTracking()
            .Where(l => l.DeletedAt == null && !l.IsTemplate &&
                        (l.OwnerId == userId || l.Participants.Any(p => !p.InvitationPending && (p.UserId == userId || p.Email == userId))));

        var listResults = await accessibleLists
            .Where(l => EF.Functions.ILike(l.Name, pattern, "\\"))
            .OrderBy(l => l.Name)
            .Take(MaxResults)
            .Select(l => new SearchResultItem(
                SearchResultKind.List,
                l.Id,
                l.Name,
                null,
                l.Name,
                "name"))
            .ToListAsync(ct);

        var remaining = MaxResults - listResults.Count;
        if (remaining <= 0)
            return listResults;

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
            .OrderBy(t => t.Title)
            .Take(remaining)
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

        return listResults.Concat(taskResults)
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
