using Klassenbibliothek.Services;

namespace Klassenbibliothek.Data;

/// <summary>
/// Applies the workspace task filter consistently across list, Kanban, table and calendar views.
/// </summary>
public static class TodoTaskFilterMatcher
{
    public static bool Matches(
        TodoTaskFilter? filter,
        TodoTaskEntity task,
        string? currentUserId,
        bool? effectiveDone = null,
        DateTime? today = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.DeletedAt is not null)
            return false;

        var f = filter;
        if (f is null || !f.IsActive())
            return true;

        var query = (f.Query ?? string.Empty).Trim();
        if (query.Length > 0
            && !$"{task.Title} {RichTextContent.ToPlainText(task.Description)}".Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (f.Done is not null && (effectiveDone ?? task.Done) != f.Done.Value)
            return false;

        if (f.NoDueDate || f.Overdue || f.DueNextDay || f.DueNextWeek || f.DueNextMonth)
        {
            var referenceDate = (today ?? DateTime.Today).Date;
            var due = task.DueDate?.Date;
            // Mehrere aktivierte Fälligkeitsoptionen werden als ODER behandelt. Die übrigen
            // Filtergruppen werden anschließend per UND mit diesem Ergebnis kombiniert.
            var dueMatches =
                (f.NoDueDate && due is null)
                || (f.Overdue && due is not null && due.Value < referenceDate)
                || (f.DueNextDay && IsDueBetween(due, referenceDate, 1))
                || (f.DueNextWeek && IsDueBetween(due, referenceDate, 7))
                || (f.DueNextMonth && IsDueBetween(due, referenceDate, 30));

            if (!dueMatches)
                return false;
        }

        var selectedLabelIds = f.LabelIds ?? [];
        // Bei mehreren ausgewählten Labels genügt mindestens ein Treffer; dies entspricht
        // der Mehrfachauswahl in allen Listen-, Tabellen-, Kanban- und Kalenderansichten.
        if (selectedLabelIds.Count > 0
            && !(task.LabelLinks ?? []).Any(link => selectedLabelIds.Contains(link.LabelId)))
        {
            return false;
        }

        var assignee = (task.Assignee ?? string.Empty).Trim();
        if (f.NoAssignee && assignee.Length > 0)
            return false;

        var currentUser = (currentUserId ?? string.Empty).Trim();
        if (f.AssignedToMe
            && (currentUser.Length == 0
                || !string.Equals(assignee, currentUser, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var members = (task.MemberUserIds ?? [])
            .Select(member => (member ?? string.Empty).Trim())
            .Where(member => member.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // MemberKeys bleibt für ältere gespeicherte Filter erhalten. Beide Felder werden vor
        // dem Vergleich zusammengeführt und ohne Beachtung der Groß-/Kleinschreibung dedupliziert.
        var selectedMembers = (f.MemberUserIds ?? [])
            .Concat(f.MemberKeys ?? [])
            .Select(member => (member ?? string.Empty).Trim())
            .Where(member => member.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedMembers.Count > 0 || f.NoMembers)
        {
            var memberMatches = (f.NoMembers && members.Count == 0)
                || members.Any(member => selectedMembers.Contains(member, StringComparer.OrdinalIgnoreCase));
            if (!memberMatches)
                return false;
        }

        return true;
    }

    private static bool IsDueBetween(DateTime? due, DateTime start, int days)
        => due is not null && due.Value >= start && due.Value <= start.AddDays(days);
}
