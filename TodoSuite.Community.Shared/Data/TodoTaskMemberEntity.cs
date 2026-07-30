using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

public class TodoTaskMemberEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid TaskId { get; set; }

    [ForeignKey(nameof(TaskId))]
    public TodoTaskEntity? Task { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty; // Member-UserId (kein Email-String)
}
