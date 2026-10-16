using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Microsoft.EntityFrameworkCore;

namespace TodoSuite.Server.Services;

public sealed record ExcelImportResult(int Count, bool FollowUpFailed);

public partial class TodoTaskService
{
    public async Task<IReadOnlyList<TodoTaskEntity>> ValidateExcelImportAsync(string userId, Guid listId,
        ExcelImportSheet sheet, bool date1904, IReadOnlyList<ExcelFieldMapping> mappings)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync();
        var (_, tasks) = await PrepareExcelImportAsync(db, userId, listId, sheet, date1904, mappings);
        return tasks;
    }

    public async Task<ExcelImportResult> ImportExcelAsync(string userId, Guid listId,
        ExcelImportSheet sheet, bool date1904, IReadOnlyList<ExcelFieldMapping> mappings)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync();
        var (list, tasks) = await PrepareExcelImportAsync(db, userId, listId, sheet, date1904, mappings);
        var nextOrder = (await db.TodoTasks.Where(t => t.ListId == listId && t.DeletedAt == null)
            .MaxAsync(t => (int?)t.ListSortOrder) ?? -1) + 1;
        var orders = await db.TodoTasks.Where(t => t.ListId == listId && t.DeletedAt == null)
            .GroupBy(t => t.Column).Select(g => new { Column = g.Key, Order = g.Max(t => t.KanbanSortOrder) })
            .ToDictionaryAsync(x => x.Column, x => x.Order + 1);
        foreach (var task in tasks)
        {
            task.ListSortOrder = nextOrder++;
            task.KanbanSortOrder = orders.GetValueOrDefault(task.Column);
            orders[task.Column] = task.KanbanSortOrder + 1;
        }
        // EF parameterizes all data values; workbook data never becomes SQL or a column name.
        // One SaveChanges transaction persists the whole validated batch or no tasks at all.
        db.TodoTasks.AddRange(tasks);
        await db.SaveChangesAsync();

        var followUpFailed = false;
        foreach (var task in tasks)
        {
            try
            {
                await _notificationService.NotifyTaskEventAsync(userId, listId, task.Id,
                    NotificationEventType.TaskCreated, "Vorgang importiert", $"Die Aufgabe \"{task.Title}\" wurde importiert.", task.Assignee);
                await _automationService.ExecuteAsync(new TodoAutomationContext(listId, list.Name, userId, task, null, TodoAutomationTriggerType.TaskCreated));
            }
            catch (Exception)
            {
                // A committed import must not be presented as failed and offered for retry.
                followUpFailed = true;
            }
        }
        try { await NotifyListUpdatedAsync(listId); }
        catch (Exception) { followUpFailed = true; }
        return new ExcelImportResult(tasks.Count, followUpFailed);
    }

    private async Task<(TodoListEntity List, IReadOnlyList<TodoTaskEntity> Tasks)> PrepareExcelImportAsync(
        ApplicationDbContext db, string userId, Guid listId, ExcelImportSheet sheet, bool date1904,
        IReadOnlyList<ExcelFieldMapping> mappings)
    {
        var list = await db.TodoLists.Include(l => l.Participants).Include(l => l.Labels).AsSplitQuery()
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null);
        if (list is null || !CanWrite(userId, list))
            throw new UnauthorizedAccessException("Keine Schreibberechtigung für diese Liste.");
        var definitions = CustomFieldsEnabled
            ? await GetCustomFieldDefinitionsAsync(db, listId, default) : new Dictionary<Guid, TodoCustomFieldDefinitionEntity>();
        list.CustomFields = definitions.Values.ToList();
        var tasks = ExcelTaskMapping.Build(sheet, date1904, mappings, list, CustomFieldsEnabled);
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            task.ListId = listId;
            try
            {
                var values = await NormalizeCustomFieldValuesAsync(db, task.CustomFieldValues, definitions, default);
                task.CustomFieldValues.Clear();
                ApplyCustomFieldValues(task, values);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Excel-Zeile {sheet.Rows[index].Number}: {ex.Message}", ex);
            }
        }
        return (list, tasks);
    }
}
