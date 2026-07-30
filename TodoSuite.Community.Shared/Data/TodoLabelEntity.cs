using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

public class TodoLabelEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid ListId { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    // z.B. "#FF6B6B" oder null
    public string? BackgroundColor { get; set; }

    [ForeignKey(nameof(ListId))]
    public TodoListEntity? List { get; set; }

    public List<TodoTaskLabelEntity> TaskLinks { get; set; } = new();
}
