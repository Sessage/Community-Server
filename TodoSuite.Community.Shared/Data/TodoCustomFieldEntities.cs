using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

public enum TodoCustomFieldType
{
    Text = 0,
    Number = 1,
    Dropdown = 2,
    Date = 3,
    Checkbox = 4,
    TaskTitleSelect = 5,
    MultiSelect = 6
}

public class TodoCustomFieldDefinitionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(List))]
    public Guid ListId { get; set; }

    public TodoListEntity? List { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public TodoCustomFieldType Type { get; set; } = TodoCustomFieldType.Text;

    public int SortOrder { get; set; }

    public bool IsRequired { get; set; }

    [ForeignKey(nameof(SourceTaskList))]
    public Guid? SourceTaskListId { get; set; }

    public TodoListEntity? SourceTaskList { get; set; }

    public List<TodoCustomFieldOptionEntity> Options { get; set; } = new();
}

public class TodoCustomFieldOptionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Field))]
    public Guid FieldId { get; set; }

    public TodoCustomFieldDefinitionEntity? Field { get; set; }

    [Required]
    public string Value { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}

public class TodoTaskCustomFieldValueEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Task))]
    public Guid TaskId { get; set; }

    public TodoTaskEntity? Task { get; set; }

    [ForeignKey(nameof(Field))]
    public Guid FieldId { get; set; }

    public TodoCustomFieldDefinitionEntity? Field { get; set; }

    public string? Value { get; set; }
}
