using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Klassenbibliothek.Data;

public class TodoListEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Opaque mobile synchronization precondition; never persisted by EF.</summary>
    [NotMapped]
    public string? SyncToken { get; set; }

    [NotMapped]
    public long? SyncVersion { get; set; }

    [JsonIgnore]
    public long ContentVersion { get; set; } = 1;

    [Required]
    public string OwnerId { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    public List<TodoTaskEntity> Tasks { get; set; } = new();

    public List<string> Columns { get; set; } = new() { "Backlog", "In Arbeit", "Erledigt" };

    /// <summary>
    /// Spaltennamen, bei denen hineingezogene Aufgaben automatisch als erledigt markiert werden.
    /// </summary>
    public List<string> DoneColumns { get; set; } = new();

    /// <summary>
    /// Benutzerbezogene Reihenfolge der Tabellenansicht-Spalten. Persistiert in ListViewPreferences.
    /// </summary>
    [NotMapped]
    public List<string> TableColumnOrder { get; set; } = new();

    /// <summary>
    /// Benutzerbezogen ausgeblendete Tabellenansicht-Spalten. Persistiert in ListViewPreferences.
    /// </summary>
    [NotMapped]
    public List<string> TableHiddenColumns { get; set; } = new();

    public List<TodoLabelEntity> Labels { get; set; } = new();

    public List<TodoCustomFieldDefinitionEntity> CustomFields { get; set; } = new();

    // Default (falls keine User-Preference vorhanden)
    public DefaultListView DefaultView { get; set; } = DefaultListView.Liste;

    public List<ListParticipantEntity> Participants { get; set; } = new();
    public List<TodoListWatcherEntity> Watchers { get; set; } = new();
    public List<BoardNotificationRuleEntity> NotificationRules { get; set; } = new();
    public List<TodoAutomationRuleEntity> AutomationRules { get; set; } = new();

    // Effektive Navigationswerte fuer den aktuellen Benutzer. Persistiert in TodoListNavigationPreferences.
    [NotMapped]
    public Guid? NavigationGroupId { get; set; }
    [NotMapped]
    public TodoListGroupEntity? NavigationGroup { get; set; }

    // Reihenfolge innerhalb Root oder innerhalb einer Gruppe
    [NotMapped]
    public int NavigationSortOrder { get; set; } = 0;

    // Soft-Delete (Papierkorb)
    public DateTime? DeletedAt { get; set; }
    public string? DeletedByUserId { get; set; }
    public string? BackgroundColor { get; set; }

    /// <summary>
    /// Gibt an, ob diese Liste eine Vorlage ist (keine Aufgaben, nur Struktur).
    /// </summary>
    public bool IsTemplate { get; set; } = false;
}
