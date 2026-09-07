using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

public enum EisenhowerQuadrant
{
    UrgentImportant = 1,
    ImportantNotUrgent = 2,
    UrgentNotImportant = 3,
    NotUrgentNotImportant = 4
}

public readonly record struct EisenhowerTaskUpdate(bool IsImportant, DateTime? DueDate, DateTime? StartDate = null);

/// <summary>
/// Shared, deterministic classification and drop semantics for dashboard priority matrices.
/// Dates are interpreted as local calendar dates because task due dates are date-only UI values.
/// </summary>
public static class EisenhowerMatrixRules
{
    public static EisenhowerQuadrant Classify(TodoTaskEntity task, DateTime today)
    {
        var urgent = task.DueDate is { } due && due.Date <= today.Date;
        return (urgent, task.IsImportant) switch
        {
            (true, true) => EisenhowerQuadrant.UrgentImportant,
            (false, true) => EisenhowerQuadrant.ImportantNotUrgent,
            (true, false) => EisenhowerQuadrant.UrgentNotImportant,
            _ => EisenhowerQuadrant.NotUrgentNotImportant
        };
    }

    public static EisenhowerTaskUpdate MoveTo(TodoTaskEntity task, EisenhowerQuadrant quadrant, DateTime today)
    {
        var date = today.Date;
        var (isImportant, dueDate) = quadrant switch
        {
            EisenhowerQuadrant.UrgentImportant => (true,
                task.DueDate is { } urgentImportantDue && urgentImportantDue.Date <= date
                    ? urgentImportantDue
                    : date),
            EisenhowerQuadrant.ImportantNotUrgent => (true,
                task.DueDate is { } importantDue && importantDue.Date > date
                    ? importantDue
                    : date.AddDays(7)),
            EisenhowerQuadrant.UrgentNotImportant => (false,
                task.DueDate is { } urgentDue && urgentDue.Date <= date
                    ? urgentDue
                    : date),
            EisenhowerQuadrant.NotUrgentNotImportant => (false, (DateTime?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(quadrant), quadrant, null)
        };

        // Keep the shared timeline range valid as the matrix adjusts due dates.
        var startDate = dueDate is null
            ? null
            : task.StartDate is { } start && start.Date > dueDate.Value.Date
                ? dueDate.Value.Date
                : task.StartDate;
        return new EisenhowerTaskUpdate(isImportant, dueDate, startDate);
    }
}
