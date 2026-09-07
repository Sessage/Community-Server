namespace Klassenbibliothek.Data;

/// <summary>Account-wide calendar used by all devices for My Day.</summary>
public sealed class DailyFocusPreferenceEntity
{
    public string UserId { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
}

/// <summary>A personal selection; it never moves or changes the underlying task.</summary>
public sealed class DailyFocusSelectionEntity
{
    public string UserId { get; set; } = "";
    public DateOnly Date { get; set; }
    public Guid TaskId { get; set; }
}
