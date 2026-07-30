using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

/// <summary>Persönlicher UI-Zustand einer Navigationsgruppe.</summary>
public class TodoListGroupPreferenceEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string UserId { get; set; } = string.Empty;
    [ForeignKey(nameof(Group))] public Guid GroupId { get; set; }
    public TodoListGroupEntity? Group { get; set; }
    public bool IsCollapsed { get; set; }
    /// <summary>Persönliche Position der Gruppe in der gemischten Navigation.</summary>
    public int? NavigationSortOrder { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
