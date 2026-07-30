using System.ComponentModel.DataAnnotations;

namespace Klassenbibliothek.Data;

/// <summary>Gemeinsame, für alle Mitglieder identische Zuordnung einer Liste zu einem Portfolio.</summary>
public class PortfolioListEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PortfolioGroupId { get; set; }
    public TodoListGroupEntity? PortfolioGroup { get; set; }
    public Guid ListId { get; set; }
    public TodoListEntity? List { get; set; }
    public int SortOrder { get; set; }
    [Required] public string AddedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
