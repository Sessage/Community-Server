using System.Globalization;
using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

public enum TimelineExportScale { Days, Weeks, Months, Years }
public enum TimelineExportPeriod { Current, Today, Days, Weeks, Months, Years }

public sealed class TimelineExportOptions
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public TimelineExportScale Scale { get; set; } = TimelineExportScale.Weeks;
    public int PixelWidth { get; set; } = 2400;
    public bool ShowAssignees { get; set; } = true;
    public bool OnlyOpen { get; set; }
    public bool ShowMilestones { get; set; } = true;
    public bool ShowToday { get; set; } = true;
    public bool ShowWeekends { get; set; } = true;
    public bool ShowDates { get; set; } = true;
    public bool GroupByAssignee { get; set; }
    public bool TransparentBackground { get; set; }
    /// <summary>Zero exports one image; otherwise splits by task count, repeating group headers.</summary>
    public int TasksPerImage { get; set; }
}

public sealed record TimelineExportRow(string Title, string Assignee, string Dates, double Start, double End, bool Milestone, bool Done, string Color, bool Group = false);
public sealed record TimelineExportTick(double Start, double End, string Label);
public sealed record TimelineExportImage(string Title, string Subtitle, string TaskLabel, string AssigneeLabel,
    string TodayLabel, int PixelWidth, int Days, int StartWeekday, double Today, bool ShowAssignees,
    bool ShowDates, bool ShowWeekends, bool ShowToday, IReadOnlyList<TimelineExportRow> Rows, IReadOnlyList<TimelineExportTick> Ticks,
    bool TransparentBackground = false, int PageNumber = 1, int PageCount = 1);

/// <summary>Creates a bounded, read-only image projection from the tasks visible to the caller.</summary>
public static class TimelineImageExport
{
    public static IReadOnlyList<TimelineExportImage> BuildPages(string title, IEnumerable<TodoTaskEntity> tasks,
        TimelineExportOptions options, Func<string?, string> assigneeName, DateTime today,
        string taskLabel, string assigneeLabel, string todayLabel)
    {
        if (options.TasksPerImage == 0)
            return [Build(title, tasks, options, assigneeName, today, taskLabel, assigneeLabel, todayLabel)];
        if (options.TasksPerImage is < 1 or > 100) throw new ArgumentException("Timeline_ExportInvalidPageSize");
        if (options.From.Date > options.To.Date) throw new ArgumentException("Timeline_ExportInvalidRange");
        var selected = tasks.Where(t => t.DeletedAt is null && (!options.OnlyOpen || !t.Done))
            .Where(t => TimelineDateRange.GetDisplayRange(t) is { } range
                && range.End >= options.From.Date && range.Start <= options.To.Date
                && (options.ShowMilestones || !TimelineDateRange.IsMilestone(t)))
            .OrderBy(t => t.ListSortOrder).ThenBy(t => t.Title).ThenBy(t => t.Id).ToList();
        if (options.GroupByAssignee && options.ShowAssignees)
            selected = selected.GroupBy(t => (t.Assignee ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => assigneeName(g.Key), StringComparer.CurrentCultureIgnoreCase).SelectMany(g => g).ToList();
        if (selected.Count == 0)
            return [Build(title, [], options, assigneeName, today, taskLabel, assigneeLabel, todayLabel)];
        var count = (selected.Count - 1) / options.TasksPerImage + 1;
        return selected.Chunk(options.TasksPerImage).Select((chunk, index) =>
            Build(title, chunk, options, assigneeName, today, taskLabel, assigneeLabel, todayLabel) with
            {
                PageNumber = index + 1, PageCount = count
            }).ToArray();
    }

    public static (DateTime From, DateTime To, TimelineExportScale Scale) Preset(
        TimelineExportPeriod period, DateTime today, (DateTime Start, DateTime End) current)
    {
        today = today.Date;
        return period switch
        {
            TimelineExportPeriod.Current => (current.Start.Date, current.End.Date, TimelineExportScale.Weeks),
            TimelineExportPeriod.Today => (today, today, TimelineExportScale.Days),
            TimelineExportPeriod.Days => (today, TimelineDateRange.AddDaysClamped(today, 13), TimelineExportScale.Days),
            TimelineExportPeriod.Weeks => WeekPreset(today),
            TimelineExportPeriod.Months => MonthPreset(today),
            TimelineExportPeriod.Years => (new DateTime(today.Year, 1, 1), new DateTime(Math.Min(9999, today.Year + 2), 12, 31), TimelineExportScale.Years),
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };
    }

    private static (DateTime, DateTime, TimelineExportScale) WeekPreset(DateTime today)
    {
        var monday = TimelineDateRange.AddDaysClamped(today, -((int)today.DayOfWeek + 6) % 7);
        return (monday, TimelineDateRange.AddDaysClamped(monday, 55), TimelineExportScale.Weeks);
    }

    private static (DateTime, DateTime, TimelineExportScale) MonthPreset(DateTime today)
    {
        var start = new DateTime(today.Year, today.Month, 1);
        var end = start.Year == 9999 && start.Month > 6 ? DateTime.MaxValue.Date : start.AddMonths(6).AddDays(-1);
        return (start, end, TimelineExportScale.Months);
    }

    public static TimelineExportImage Build(string title, IEnumerable<TodoTaskEntity> tasks, TimelineExportOptions options,
        Func<string?, string> assigneeName, DateTime today, string taskLabel, string assigneeLabel, string todayLabel)
    {
        var from = options.From.Date;
        var to = options.To.Date;
        if (from > to) throw new ArgumentException("Timeline_ExportInvalidRange");
        var days = TimelineDateRange.GetInclusiveDayCount((from, to));
        if (days > 36600 || !Enum.IsDefined(options.Scale)) throw new ArgumentException("Timeline_ExportRangeTooLong");
        if (options.PixelWidth is not (1600 or 2400 or 3200)) throw new ArgumentException("Timeline_ExportTooLarge");

        var selected = tasks.Where(t => t.DeletedAt is null && (!options.OnlyOpen || !t.Done))
            .Select(t => (Task: t, Range: TimelineDateRange.GetDisplayRange(t), Milestone: TimelineDateRange.IsMilestone(t)))
            .Where(t => t.Range.HasValue && t.Range.Value.End >= from && t.Range.Value.Start <= to && (options.ShowMilestones || !t.Milestone))
            .OrderBy(t => t.Task.ListSortOrder).ThenBy(t => t.Task.Title).ToList();
        var rows = new List<TimelineExportRow>();
        TimelineExportRow Row(TodoTaskEntity task, (DateTime Start, DateTime End) range, bool milestone)
            => new(task.Title, assigneeName(task.Assignee), $"{range.Start:d} – {range.End:d}",
                Math.Max(0, (range.Start - from).TotalDays), Math.Min(days, (range.End - from).TotalDays + 1),
                milestone, task.Done, SafeColor(task.CardColor) ? task.CardColor! : task.Done ? "#16a34a" : "#2563eb");
        if (options.GroupByAssignee && options.ShowAssignees)
        {
            foreach (var group in selected.GroupBy(t => (t.Task.Assignee ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => assigneeName(g.Key), StringComparer.CurrentCultureIgnoreCase))
            {
                rows.Add(new(assigneeName(group.Key), "", "", 0, 0, false, false, "#2563eb", true));
                rows.AddRange(group.Select(t => Row(t.Task, t.Range!.Value, t.Milestone)));
            }
        }
        else rows.AddRange(selected.Select(t => Row(t.Task, t.Range!.Value, t.Milestone)));
        var pixelHeight = ((160L + rows.Count * 44L) * options.PixelWidth + 999) / 1000;
        if (pixelHeight > 16384 || pixelHeight * options.PixelWidth > 40000000)
            throw new ArgumentException("Timeline_ExportTooLarge");

        var ticks = new List<TimelineExportTick>();
        var cursor = from;
        while (cursor <= to)
        {
            DateTime next;
            string label;
            switch (options.Scale)
            {
                case TimelineExportScale.Days:
                    next = TimelineDateRange.AddDaysClamped(cursor, 1); label = cursor.ToString("dd.MM."); break;
                case TimelineExportScale.Weeks:
                    next = TimelineDateRange.AddDaysClamped(cursor, 7 - ((int)cursor.DayOfWeek + 6) % 7);
                    label = $"{ISOWeek.GetYear(cursor)} · {ISOWeek.GetWeekOfYear(cursor):00}"; break;
                case TimelineExportScale.Months:
                    next = cursor.Year == 9999 && cursor.Month == 12 ? DateTime.MaxValue.Date : new DateTime(cursor.Year, cursor.Month, 1).AddMonths(1);
                    label = cursor.ToString("MMM yyyy"); break;
                default:
                    next = cursor.Year == 9999 ? DateTime.MaxValue.Date : new DateTime(cursor.Year + 1, 1, 1);
                    label = cursor.ToString("yyyy"); break;
            }
            var endOffset = next <= cursor || next > to ? days : (next - from).Days;
            ticks.Add(new((cursor - from).Days, endOffset, label));
            if (ticks.Count > 500) throw new ArgumentException("Timeline_ExportCoarserScale");
            if (next <= cursor || next > to) break;
            cursor = next;
        }
        return new(title, $"{from:d} – {to:d} · {selected.Count} {taskLabel}", taskLabel, assigneeLabel, todayLabel,
            options.PixelWidth, days, (int)from.DayOfWeek, (today.Date - from).Days,
            options.ShowAssignees, options.ShowDates, options.ShowWeekends, options.ShowToday, rows, ticks, options.TransparentBackground);
    }

    private static bool SafeColor(string? color)
        => color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
}
