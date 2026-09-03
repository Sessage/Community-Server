using Klassenbibliothek.Data;
using System.Net;

namespace Klassenbibliothek.Services;

/// <summary>Provides normalization and matching helpers shared by server and client search implementations.</summary>
public static class SearchUtilities
{
    public const int MaxQueryLength = 200;
    public const int MaxResults = 100;

    public static string NormalizeQuery(string? query)
    {
        var normalized = (query ?? string.Empty).Trim();
        return normalized.Length <= MaxQueryLength
            ? normalized
            : normalized[..MaxQueryLength];
    }

    public static bool CanUseCacheFallback(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.NotFound
            || statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;

    public static IReadOnlyList<SearchResultItem> SearchCachedLists(
        IEnumerable<TodoListEntity> lists,
        string? query,
        int maxResults = MaxResults)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0 || maxResults <= 0)
            return [];

        var listResults = lists
            .Where(list => list.DeletedAt is null && !list.IsTemplate)
            .Where(list => Contains(list.Name, normalized))
            .OrderBy(list => MatchRank(list.Name, normalized))
            .ThenBy(list => list.Name, StringComparer.OrdinalIgnoreCase)
            .Select(list => new SearchResultItem(
                SearchResultKind.List,
                list.Id,
                list.Name,
                null,
                list.Name,
                "name"))
            .ToList();

        var taskResults = lists
            .Where(list => list.DeletedAt is null && !list.IsTemplate)
            .SelectMany(list => (list.Tasks ?? []).Where(task => task.DeletedAt is null)
                .Select(task => new { List = list, Task = task }))
            .Select(candidate => new
            {
                candidate.List,
                candidate.Task,
                MatchField = MatchField(candidate.Task, normalized)
            })
            .Where(candidate => candidate.MatchField is not null)
            .OrderBy(candidate => TaskMatchRank(candidate.Task, candidate.MatchField!, normalized))
            .ThenBy(candidate => candidate.Task.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.List.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new SearchResultItem(
                SearchResultKind.Task,
                candidate.List.Id,
                candidate.List.Name,
                candidate.Task.Id,
                candidate.Task.Title,
                candidate.MatchField))
            .ToList();

        return TakeBalanced(listResults, taskResults, maxResults);
    }

    public static IReadOnlyList<SearchResultItem> TakeBalanced(
        IReadOnlyList<SearchResultItem> listResults,
        IReadOnlyList<SearchResultItem> taskResults,
        int maxResults = MaxResults)
    {
        if (maxResults <= 0)
            return [];

        var half = maxResults / 2;
        var selectedLists = listResults.Take(Math.Min(listResults.Count, half)).ToList();
        var selectedTasks = taskResults.Take(Math.Min(taskResults.Count, maxResults - selectedLists.Count)).ToList();
        var remaining = maxResults - selectedLists.Count - selectedTasks.Count;

        if (remaining > 0)
            selectedLists.AddRange(listResults.Skip(selectedLists.Count).Take(remaining));

        remaining = maxResults - selectedLists.Count - selectedTasks.Count;
        if (remaining > 0)
            selectedTasks.AddRange(taskResults.Skip(selectedTasks.Count).Take(remaining));

        return selectedLists.Concat(selectedTasks).ToList();
    }

    private static string? MatchField(TodoTaskEntity task, string query)
    {
        if (Contains(task.Title, query)) return "title";
        if (Contains(RichTextContent.ToPlainText(task.Description), query)) return "description";
        return (task.Steps ?? []).Any(step => Contains(step.Title, query)) ? "step" : null;
    }

    private static int TaskMatchRank(TodoTaskEntity task, string matchField, string query)
        => matchField == "title" ? MatchRank(task.Title, query) : matchField == "description" ? 3 : 4;

    private static int MatchRank(string? value, string query)
    {
        if (string.Equals(value, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (value?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true) return 1;
        return 2;
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
