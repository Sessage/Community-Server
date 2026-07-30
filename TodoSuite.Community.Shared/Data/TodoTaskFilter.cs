namespace Klassenbibliothek.Data;

public class TodoTaskFilter
{
    public string Query { get; set; } = "";

    // Done-Filter: null = alles, true = nur done, false = nur offen
    public bool? Done { get; set; }

    // Due buckets
    public bool NoDueDate { get; set; }
    public bool Overdue { get; set; }
    public bool DueNextDay { get; set; }
    public bool DueNextWeek { get; set; }
    public bool DueNextMonth { get; set; }
    public HashSet<string> MemberUserIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Members / assignment
    public bool NoMembers { get; set; }
    public bool NoAssignee { get; set; }

    public bool AssignedToMe { get; set; }
    public HashSet<string> MemberKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase); // userId oder email

    // Labels
    public HashSet<Guid> LabelIds { get; set; } = new();

    public bool IsActive()
    {
        if (!string.IsNullOrWhiteSpace(Query)) return true;
        if (Done is not null) return true;
        if (NoAssignee) return true;
        if (NoDueDate || Overdue || DueNextDay || DueNextWeek || DueNextMonth)
            return true;

        if (NoMembers || AssignedToMe || MemberUserIds.Count > 0)
            return true;

        if (LabelIds.Count > 0) return true;

        return false;
    }

}
