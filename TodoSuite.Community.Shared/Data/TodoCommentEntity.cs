using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

/// <summary>Persisted comment associated with a task and its author.</summary>
public class TodoCommentEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Message { get; set; } = string.Empty;

    public string? Author { get; set; }

    public string? AuthorUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(Task))]
    public Guid TaskId { get; set; }

    public TodoTaskEntity? Task { get; set; }
}
