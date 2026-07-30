using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Klassenbibliothek.Data;

public class TodoTaskEntity
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
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<TodoTaskLabelEntity> LabelLinks { get; set; } = new();

    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }

    //  Erinnerung (UTC!)
    public DateTime? ReminderAtUtc { get; set; }

    // wurde bereits verarbeitet (E-Mail/Push raus)?
    public DateTime? ReminderSentAtUtc { get; set; }

    public bool Done { get; set; }
    public bool IsImportant { get; set; }

    // Manuelle Kartenfarbe (z.B. "#46d39a"), null = keine
    public string? CardColor { get; set; }

    // Wie soll die Farbe angewendet werden?
    public TaskCardColorMode CardColorMode { get; set; } = TaskCardColorMode.TopOnly;

    public int ListSortOrder { get; set; }
    public int KanbanSortOrder { get; set; }

    public string? Assignee { get; set; }
    public List<TodoTaskMemberEntity> Members { get; set; } = new();
    public List<TodoTaskWatcherEntity> Watchers { get; set; } = new();

    [NotMapped]
    public List<string> MemberUserIds { get; set; } = new();

    public RecurrencePattern Recurrence { get; set; } = RecurrencePattern.Keine;

    public string? CustomRecurrence { get; set; }

    [Required]
    public string Column { get; set; } = "Backlog";

    public List<TodoAttachmentEntity> Attachments { get; set; } = new();

    public List<TodoStepEntity> Steps { get; set; } = new();

    public List<TodoCommentEntity> Comments { get; set; } = new();

    public List<TodoTaskCustomFieldValueEntity> CustomFieldValues { get; set; } = new();

    [ForeignKey(nameof(List))]
    public Guid ListId { get; set; }

    public TodoListEntity? List { get; set; }

    // Optional (wenn du "CreatedAt" sortieren willst)
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Soft-Delete (Papierkorb)
    public DateTime? DeletedAt { get; set; }
    public string? DeletedByUserId { get; set; }
}
