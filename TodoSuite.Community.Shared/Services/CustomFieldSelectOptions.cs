using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

public static class CustomFieldSelectOptions
{
    public static string TaskValue(Guid taskId) => taskId.ToString("N");

    public static IReadOnlyList<(string Value, string Label)> GetOptions(
        TodoCustomFieldDefinitionEntity field,
        IEnumerable<TodoListEntity>? availableLists = null,
        string? currentValue = null)
    {
        var options = field.Type switch
        {
            TodoCustomFieldType.Dropdown or TodoCustomFieldType.MultiSelect => field.Options
                .OrderBy(o => o.SortOrder)
                .Select(o => ((o.Value ?? "").Trim(), (o.Value ?? "").Trim())),

            TodoCustomFieldType.TaskTitleSelect => GetSourceTasks(field, availableLists)
                .Where(task => !string.IsNullOrWhiteSpace(task.Title))
                .GroupBy(task => task.Id)
                .Select(group => group.First())
                .Select(task => (TaskValue(task.Id), (task.Title ?? "").Trim()))
                .OrderBy(option => option.Item2),

            _ => []
        };

        var selected = (currentValue ?? "").Trim();
        var result = options
            .Where(option => !string.IsNullOrWhiteSpace(option.Item1) && !string.IsNullOrWhiteSpace(option.Item2))
            .ToList();

        if (field.Type == TodoCustomFieldType.TaskTitleSelect
            && !string.IsNullOrWhiteSpace(selected)
            && !Guid.TryParse(selected, out _))
        {
            var legacyMatch = result.FirstOrDefault(option => string.Equals(option.Item2, selected, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(legacyMatch.Item1))
                result.Insert(0, (selected, legacyMatch.Item2));
        }

        if (!string.IsNullOrWhiteSpace(selected)
            && result.All(option => !string.Equals(option.Item1, selected, StringComparison.OrdinalIgnoreCase)))
        {
            result.Insert(0, (selected, $"{selected} (nicht mehr verfuegbar)"));
        }

        return result;
    }

    public static bool ContainsValue(TodoCustomFieldDefinitionEntity field, string? value, IEnumerable<TodoListEntity>? availableLists = null)
    {
        var normalized = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (field.Type == TodoCustomFieldType.MultiSelect)
        {
            var validValues = GetOptions(field, availableLists)
                .Select(option => option.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return CustomFieldMultiSelectValues.Parse(normalized)
                .All(validValues.Contains);
        }

        if (GetOptions(field, availableLists)
            .Any(option => string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return field.Type == TodoCustomFieldType.TaskTitleSelect
            && !Guid.TryParse(normalized, out _)
            && GetSourceTasks(field, availableLists)
                .Any(task => string.Equals((task.Title ?? "").Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<TodoTaskEntity> GetSourceTasks(TodoCustomFieldDefinitionEntity field, IEnumerable<TodoListEntity>? availableLists)
    {
        if (field.SourceTaskListId is null)
            return [];

        var sourceList = (availableLists ?? [])
            .FirstOrDefault(list => list.Id == field.SourceTaskListId)
            ?? field.SourceTaskList;

        return (sourceList?.Tasks ?? [])
            .Where(task => task.DeletedAt is null);
    }
}
