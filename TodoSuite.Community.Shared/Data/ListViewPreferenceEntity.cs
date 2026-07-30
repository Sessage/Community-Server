using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

/*
 * Entität, um die bevorzugte Listen-Ansicht + Sortierung pro Liste & User zu speichern
 */
public class ListViewPreferenceEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    [ForeignKey(nameof(List))]
    public Guid ListId { get; set; }

    public TodoListEntity? List { get; set; }

    public DefaultListView LastView { get; set; } = DefaultListView.Liste;

    // Persistente Sortierung pro View
    public ListSortMode ListSortMode { get; set; } = ListSortMode.Custom;
    public ListSortMode KanbanSortMode { get; set; } = ListSortMode.Custom;

    public List<string> TableColumnOrder { get; set; } = new();
    public List<string> TableHiddenColumns { get; set; } = new();

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
