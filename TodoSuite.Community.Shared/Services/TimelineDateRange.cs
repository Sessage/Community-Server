using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

public enum TimelineDateChangeMode
{
    Move,
    ResizeStart,
    ResizeEnd
}

/// <summary>Pure date-range rules shared by the interactive timeline and its tests.</summary>
public static class TimelineDateRange
{
    public static bool IsMilestone(TodoTaskEntity task)
        => task.DueDate.HasValue
           && (!task.StartDate.HasValue || task.StartDate.Value.Date == task.DueDate.Value.Date);

    public static (DateTime Start, DateTime End)? GetDisplayRange(TodoTaskEntity task)
    {
        if (!task.StartDate.HasValue && !task.DueDate.HasValue)
            return null;

        var start = (task.StartDate ?? task.DueDate)!.Value.Date;
        var end = (task.DueDate ?? task.StartDate)!.Value.Date;
        return start <= end ? (start, end) : (end, start);
    }

    public static TimelineDateUpdate Apply(TodoTaskEntity task, TimelineDateChangeMode mode, int deltaDays)
    {
        var range = GetDisplayRange(task)
            ?? throw new InvalidOperationException("Eine Aufgabe ohne Termin kann nicht verschoben werden.");

        deltaDays = Math.Clamp(deltaDays, -36500, 36500);
        if (mode == TimelineDateChangeMode.Move)
        {
            return new TimelineDateUpdate(
                task.StartDate?.Date.AddDays(deltaDays),
                task.DueDate?.Date.AddDays(deltaDays));
        }

        if (mode == TimelineDateChangeMode.ResizeStart)
        {
            var newStart = range.Start.AddDays(deltaDays);
            if (newStart > range.End)
                newStart = range.End;
            return new TimelineDateUpdate(newStart, range.End);
        }

        var newEnd = range.End.AddDays(deltaDays);
        if (newEnd < range.Start)
            newEnd = range.Start;
        return new TimelineDateUpdate(range.Start, newEnd);
    }

    public static TimelineDateUpdate CreateSchedule(DateTime today)
        => new(today.Date, today.Date.AddDays(2));

    public static TimelineDateUpdate CreateMilestone(DateTime today)
        => new(today.Date, today.Date);

    public static TimelineDateUpdate ConvertToMilestone(TodoTaskEntity task, DateTime today)
    {
        var anchor = GetDisplayRange(task)?.End ?? today.Date;
        return new TimelineDateUpdate(anchor, anchor);
    }

    public static TimelineDateUpdate ConvertToSchedule(TodoTaskEntity task, DateTime today)
    {
        var anchor = GetDisplayRange(task)?.Start ?? today.Date;
        return new TimelineDateUpdate(anchor, anchor.AddDays(2));
    }

    public static TimelineDateUpdate RemoveSchedule()
        => new(null, null);
}

public readonly record struct TimelineDateUpdate(DateTime? StartDate, DateTime? DueDate);
