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
    public static int GetInclusiveDayCount((DateTime Start, DateTime End) range)
        => Math.Max(1, (range.End.Date - range.Start.Date).Days + 1);

    public static DateTime AddDaysClamped(DateTime value, int days)
    {
        var date = value.Date;
        var ticks = date.Ticks + (long)days * TimeSpan.TicksPerDay;
        ticks = Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Date.Ticks);
        return new DateTime(ticks, date.Kind);
    }

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
                task.StartDate.HasValue ? AddDaysClamped(task.StartDate.Value, deltaDays) : null,
                task.DueDate.HasValue ? AddDaysClamped(task.DueDate.Value, deltaDays) : null);
        }

        if (mode == TimelineDateChangeMode.ResizeStart)
        {
            var newStart = AddDaysClamped(range.Start, deltaDays);
            if (newStart > range.End)
                newStart = range.End;
            return new TimelineDateUpdate(newStart, range.End);
        }

        var newEnd = AddDaysClamped(range.End, deltaDays);
        if (newEnd < range.Start)
            newEnd = range.Start;
        return new TimelineDateUpdate(range.Start, newEnd);
    }

    public static TimelineDateUpdate CreateSchedule(DateTime today)
        => new(today.Date, AddDaysClamped(today, 2));

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
        return new TimelineDateUpdate(anchor, AddDaysClamped(anchor, 2));
    }

    public static TimelineDateUpdate RemoveSchedule()
        => new(null, null);
}

public readonly record struct TimelineDateUpdate(DateTime? StartDate, DateTime? DueDate);
