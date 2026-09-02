using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Klassenbibliothek.Data;

public enum TodoAutomationTriggerType
{
    TaskCreated = 0,
    TaskUpdated = 1,
    ColumnChanged = 2,
    TaskCompleted = 3,
    TaskReopened = 4,
    AssigneeChanged = 5,
    TaskCreatedByForm = 6,
    TaskCreatedByEmail = 7,
    ApprovalGranted = 8,
    ApprovalRejected = 9
}

public enum TodoAutomationConditionType
{
    FromColumnEquals = 0,
    ToColumnEquals = 1,
    TaskIsCompleted = 2,
    TaskIsOpen = 3,
    AssigneeEquals = 4,
    CustomFieldEquals = 5,
    CustomFieldIsEmpty = 6,
    TitleContains = 7
}

public enum TodoAutomationActionType
{
    SetCardColor = 0,
    SetCustomField = 1,
    ClearCustomField = 2,
    AddComment = 3,
    AddLabel = 4,
    MarkCompleted = 5,
    MarkOpen = 6,
    MoveToColumn = 7,
    SetAssignee = 8,
    SendNotification = 9,
    PostWebhook = 10,
    SetImportant = 11,
    ClearAssignee = 12,
    SetApprover = 13,
    RequestApproval = 14,
    PluginAction = 15,
    MoveToList = 16
}

public class TodoAutomationRuleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(List))]
    public Guid ListId { get; set; }
    public TodoListEntity? List { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
    public TodoAutomationTriggerType TriggerType { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<TodoAutomationConditionEntity> Conditions { get; set; } = [];
    public List<TodoAutomationActionEntity> Actions { get; set; } = [];
}

public class TodoAutomationConditionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public TodoAutomationRuleEntity? Rule { get; set; }
    public TodoAutomationConditionType Type { get; set; }
    public int SortOrder { get; set; }
    public Guid? CustomFieldId { get; set; }
    [MaxLength(100)] public string? FieldKey { get; set; }
    [MaxLength(1000)] public string? Value { get; set; }
}

public class TodoAutomationActionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public TodoAutomationRuleEntity? Rule { get; set; }
    public TodoAutomationActionType Type { get; set; }
    public int SortOrder { get; set; }
    public Guid? CustomFieldId { get; set; }
    [MaxLength(100)] public string? FieldKey { get; set; }
    public Guid? LabelId { get; set; }
    [MaxLength(300)] public string? PluginActionId { get; set; }
    [MaxLength(4000)] public string? Value { get; set; }
    public string ConfigurationJson { get; set; } = "{}";

    public TodoAutomationWebhookConfiguration GetWebhookConfiguration()
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<TodoAutomationWebhookConfiguration>(
                                    string.IsNullOrWhiteSpace(ConfigurationJson) ? "{}" : ConfigurationJson)
                                ?? new TodoAutomationWebhookConfiguration();
            configuration.SelectedFields ??= [];
            return configuration;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new TodoAutomationWebhookConfiguration();
        }
    }

    public TodoAutomationCardColorConfiguration GetCardColorConfiguration()
    {
        try
        {
            return JsonSerializer.Deserialize<TodoAutomationCardColorConfiguration>(
                       string.IsNullOrWhiteSpace(ConfigurationJson) ? "{}" : ConfigurationJson)
                   ?? new TodoAutomationCardColorConfiguration();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new TodoAutomationCardColorConfiguration();
        }
    }
}

public sealed class TodoAutomationCardColorConfiguration
{
    public TaskCardColorMode Mode { get; set; } = TaskCardColorMode.TopOnly;
}

public sealed class TodoAutomationWebhookConfiguration
{
    public string Url { get; set; } = string.Empty;
    public string Fields { get; set; } = "id,title,column,done,assignee,customFields";
    public List<string> SelectedFields { get; set; } = [];
    public string BearerToken { get; set; } = string.Empty;
    public string ProtectedBearerToken { get; set; } = string.Empty;
    public bool BearerTokenConfigured { get; set; }

    public IReadOnlyList<string> GetSelectedFields()
    {
        SelectedFields ??= [];
        if (SelectedFields.Count > 0)
            return SelectedFields;

        return (Fields ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record TodoAutomationContext(
    Guid ListId,
    string ListName,
    string ActorUserId,
    TodoTaskEntity Task,
    TodoTaskEntity? PreviousTask,
    TodoAutomationTriggerType TriggerType);
