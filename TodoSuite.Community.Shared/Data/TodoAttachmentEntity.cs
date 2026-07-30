using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

public class TodoAttachmentEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string FileName { get; set; } = string.Empty;

    public string? Url { get; set; }

    [ForeignKey(nameof(Task))]
    public Guid TaskId { get; set; }

    public TodoTaskEntity? Task { get; set; }
}
