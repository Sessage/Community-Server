using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

public class TodoListGroupEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string OwnerId { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    // Reihenfolge der Gruppen in der Navigation
    public int SortOrder { get; set; }

    /// <summary>Kennzeichnet eine Gruppe mit dauerhaftem Dashboard.</summary>
    public bool IsPortfolio { get; set; }

    // Legacy-Feld; der wirksame Zustand wird nutzerspezifisch gespeichert.
    public bool IsCollapsed { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public List<TodoListEntity> Lists { get; set; } = new();

    [NotMapped]
    public bool CanManage { get; set; }
}
