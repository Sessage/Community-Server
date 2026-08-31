using System.Text.Json;

namespace Klassenbibliothek.Data;

/// <summary>
/// Gespeicherte Dashboard-Ansicht eines Benutzers über mehrere Listen hinweg.
/// </summary>
public class DashboardEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    /// <summary>Zugeordnetes Portfolio; null bei einem frei verwaltbaren Dashboard.</summary>
    public Guid? PortfolioGroupId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>IDs der in diesem Dashboard ausgewählten Listen.</summary>
    public List<Guid> SelectedListIds { get; set; } = new();

    /// <summary>Sortierungsregel für Aufgaben im Dashboard.</summary>
    public ListSortMode SortMode { get; set; } = ListSortMode.DueDate;

    /// <summary>Gruppierungsregel für Aufgaben im Dashboard.</summary>
    public DashboardGroupBy GroupBy { get; set; } = DashboardGroupBy.List;

    /// <summary>Gespeicherte Filtereinstellungen als JSON.</summary>
    public string FilterJson { get; set; } = "{}";

    // ── Hilfsmethoden ──────────────────────────────────────────────────────

    public DashboardFilter GetFilter()
    {
        try
        {
            var filter = JsonSerializer.Deserialize<DashboardFilter>(FilterJson)
                         ?? new DashboardFilter();
            filter.NormalizeWidgetOrder();
            return filter;
        }
        catch
        {
            return new DashboardFilter();
        }
    }

    public void SetFilter(DashboardFilter filter)
        => FilterJson = JsonSerializer.Serialize(filter,
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
}

/// <summary>
/// Wie Aufgaben im Dashboard gruppiert werden sollen.
/// </summary>
public enum DashboardGroupBy
{
    None = 0,
    List = 1,
    Status = 2,
    DueDate = 3
}

/// <summary>
/// In-Memory-Filtermodell für ein Dashboard (wird in DashboardEntity.FilterJson gespeichert).
/// </summary>
public class DashboardFilter
{
    public string Query { get; set; } = "";
    public bool? Done { get; set; }
    public bool Overdue { get; set; }
    public bool DueNextDay { get; set; }
    public bool DueNextWeek { get; set; }
    public bool DueNextMonth { get; set; }
    public bool NoDueDate { get; set; }
    public bool AssignedToMe { get; set; }
    public bool NoAssignee { get; set; }
    public bool IsImportant { get; set; }

    // Widget-Konfiguration (Jira-ähnliche, individuell anpassbare Dashboards)
    public bool ShowKpiCards { get; set; } = true;
    public bool ShowStatusChart { get; set; } = true;
    public bool ShowPriorityChart { get; set; } = true;
    public bool ShowDueChart { get; set; } = true;

    public List<string> WidgetOrder { get; set; } = DefaultWidgetOrder();

    public static List<string> DefaultWidgetOrder()
        => ["kpi", "status", "priority", "due", "tasks"];

    public void NormalizeWidgetOrder()
    {
        var defaults = DefaultWidgetOrder();
        var normalized = (WidgetOrder ?? [])
            .Where(defaults.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var id in defaults.Where(d => !normalized.Contains(d, StringComparer.Ordinal)))
            normalized.Add(id);
        WidgetOrder = normalized;
    }

    public void ClearCriteria()
    {
        Query = "";
        Done = null;
        Overdue = false;
        DueNextDay = false;
        DueNextWeek = false;
        DueNextMonth = false;
        NoDueDate = false;
        AssignedToMe = false;
        NoAssignee = false;
        IsImportant = false;
    }

    public bool IsActive() =>
        !string.IsNullOrWhiteSpace(Query) ||
        Done is not null ||
        Overdue || DueNextDay || DueNextWeek || DueNextMonth || NoDueDate ||
        AssignedToMe || NoAssignee || IsImportant;

    public DashboardFilter Clone() => new()
    {
        Query = Query,
        Done = Done,
        Overdue = Overdue,
        DueNextDay = DueNextDay,
        DueNextWeek = DueNextWeek,
        DueNextMonth = DueNextMonth,
        NoDueDate = NoDueDate,
        AssignedToMe = AssignedToMe,
        NoAssignee = NoAssignee,
        IsImportant = IsImportant,
        ShowKpiCards = ShowKpiCards,
        ShowStatusChart = ShowStatusChart,
        ShowPriorityChart = ShowPriorityChart,
        ShowDueChart = ShowDueChart,
        WidgetOrder = new List<string>(WidgetOrder ?? DefaultWidgetOrder())
    };
}
