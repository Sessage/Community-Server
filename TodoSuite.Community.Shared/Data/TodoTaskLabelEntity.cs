namespace Klassenbibliothek.Data;

public class TodoTaskLabelEntity
{
    public Guid TaskId { get; set; }
    public TodoTaskEntity? Task { get; set; }

    public Guid LabelId { get; set; }
    public TodoLabelEntity? Label { get; set; }
}
