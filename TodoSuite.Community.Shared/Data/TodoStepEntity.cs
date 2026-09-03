using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

/// <summary>Ordered checklist step owned by a task.</summary>
public class TodoStepEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Title { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(Task))]
    public Guid TaskId { get; set; }

    public TodoTaskEntity? Task { get; set; }
}
