using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

/// <summary>Centralizes task input limits shared by browser, API, and native clients.</summary>
public static class TodoTaskInputValidation
{
    public const int MaxTitleLength = 500;

    public static string NormalizeTitle(string? title, string parameterName)
    {
        var normalized = (title ?? string.Empty).Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("Der Aufgabentitel darf nicht leer sein.", parameterName);
        if (normalized.Length > MaxTitleLength)
            throw new ArgumentException($"Der Aufgabentitel darf höchstens {MaxTitleLength} Zeichen enthalten.", parameterName);
        return normalized;
    }

    public static string ResolveColumn(TodoListEntity list, string? requestedColumn)
    {
        ArgumentNullException.ThrowIfNull(list);
        var columns = (list.Columns ?? [])
            .Select(column => column?.Trim())
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (columns.Count == 0)
            throw new InvalidOperationException("Die Liste enthält keine Spalte.");

        if (string.IsNullOrWhiteSpace(requestedColumn))
            return columns[0]!;

        return columns.FirstOrDefault(column =>
                   string.Equals(column, requestedColumn.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException("Die Aufgabe verweist auf eine unbekannte Spalte.", nameof(requestedColumn));
    }

    public static string? ResolveAssignee(TodoListEntity list, string? requestedAssignee, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(list);
        var key = (requestedAssignee ?? string.Empty).Trim();
        if (key.Length == 0)
            return null;

        if (string.Equals(list.OwnerId?.Trim(), key, StringComparison.OrdinalIgnoreCase))
            return list.OwnerId!.Trim();

        var participant = (list.Participants ?? [])
            .FirstOrDefault(candidate => !candidate.InvitationPending
                && ((!string.IsNullOrWhiteSpace(candidate.UserId)
                     && string.Equals(candidate.UserId.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(candidate.Email)
                        && string.Equals(candidate.Email.Trim(), key, StringComparison.OrdinalIgnoreCase))));

        if (participant is null || string.IsNullOrWhiteSpace(participant.UserId))
            throw new ArgumentException(
                "Der Bearbeiter muss eine Person aus der Liste sein, die ihre Einladung angenommen hat.",
                parameterName);

        return participant.UserId.Trim();
    }

    public static bool TryResolveAssignee(TodoListEntity list, string? requestedAssignee, out string? assignee)
    {
        try
        {
            assignee = ResolveAssignee(list, requestedAssignee, nameof(requestedAssignee));
            return true;
        }
        catch (ArgumentException)
        {
            assignee = null;
            return false;
        }
    }
}
