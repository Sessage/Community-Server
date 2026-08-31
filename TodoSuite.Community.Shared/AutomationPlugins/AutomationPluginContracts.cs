using System.Text.Json;
using Klassenbibliothek.Data;

namespace Klassenbibliothek.AutomationPlugins;

/// <summary>Versioned contract implemented by trusted Enterprise automation plugins.</summary>
public interface ISessageAutomationPlugin
{
    AutomationPluginMetadata Metadata { get; }
    IReadOnlyList<AutomationPluginActionDefinition> Actions { get; }

    Task<AutomationPluginResult> ExecuteAsync(
        string actionId,
        AutomationPluginExecutionContext context,
        IReadOnlyDictionary<string, string?> configuration,
        CancellationToken cancellationToken);
}

/// <summary>Host-side catalog abstraction. Community implementations expose an empty catalog.</summary>
public interface IAutomationPluginCatalog
{
    IReadOnlyList<AutomationPluginActionDescriptor> Actions { get; }
    bool TryGetAction(string id, out AutomationPluginActionDescriptor descriptor);
    Task<AutomationPluginResult> ExecuteAsync(
        string id,
        AutomationPluginExecutionContext context,
        IReadOnlyDictionary<string, string?> configuration,
        CancellationToken cancellationToken);
}

public sealed record AutomationPluginMetadata(string Id, string Name, string Version, string? Description = null);

public enum AutomationPluginInputType
{
    Text = 0,
    MultilineText = 1,
    Number = 2,
    Boolean = 3,
    Choice = 4,
    Column = 5,
    Person = 6,
    Secret = 7
}

public sealed record AutomationPluginInputOption(string Value, string Label);

public sealed record AutomationPluginInputDefinition(
    string Key,
    string Label,
    AutomationPluginInputType Type = AutomationPluginInputType.Text,
    bool Required = false,
    string? Description = null,
    string? DefaultValue = null,
    IReadOnlyList<AutomationPluginInputOption>? Options = null);

public sealed record AutomationPluginActionDefinition(
    string Id,
    string Name,
    string? Description = null,
    IReadOnlyList<AutomationPluginInputDefinition>? Inputs = null);

/// <summary>Serializable descriptor returned to Enterprise Web and Mobile editors.</summary>
public sealed record AutomationPluginActionDescriptor(
    string Id,
    string PluginId,
    string PluginName,
    string PluginVersion,
    string ActionId,
    string Name,
    string? Description,
    IReadOnlyList<AutomationPluginInputDefinition> Inputs);

public sealed record AutomationPluginTaskSnapshot(
    Guid Id,
    string Title,
    string? Description,
    string Column,
    bool Done,
    bool IsImportant,
    string? AssigneeUserId,
    string? ApproverUserId,
    TodoApprovalStatus ApprovalStatus,
    DateTime? StartDate,
    DateTime? DueDate,
    IReadOnlyDictionary<Guid, string?> CustomFields);

public sealed record AutomationPluginParticipant(string UserId, string DisplayName, string? Email);

public sealed record AutomationPluginExecutionContext(
    Guid ListId,
    string ListName,
    string ActorUserId,
    TodoAutomationTriggerType TriggerType,
    IReadOnlyList<string> Columns,
    IReadOnlyList<AutomationPluginParticipant> Participants,
    AutomationPluginTaskSnapshot Task,
    AutomationPluginTaskSnapshot? PreviousTask);

/// <summary>
/// A plugin can return regular Sessage automation actions. PluginAction and PostWebhook are
/// intentionally rejected by the host to prevent recursion and bypassing webhook validation.
/// </summary>
public sealed record AutomationPluginCommand(
    TodoAutomationActionType Type,
    string? Value = null,
    string? FieldKey = null,
    Guid? LabelId = null,
    string ConfigurationJson = "{}");

public sealed record AutomationPluginResult(
    IReadOnlyList<AutomationPluginCommand>? Commands = null,
    string? Message = null)
{
    public static AutomationPluginResult Empty { get; } = new([]);
}

/// <summary>Persisted action configuration. ProtectedSecrets never leaves the server.</summary>
public sealed class AutomationPluginActionConfiguration
{
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ConfiguredSecrets { get; set; } = [];
    public Dictionary<string, string> ProtectedSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static AutomationPluginActionConfiguration Parse(string? json)
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<AutomationPluginActionConfiguration>(json ?? "{}") ?? new();
            configuration.Values = NormalizeDictionary(configuration.Values);
            configuration.ConfiguredSecrets = (configuration.ConfiguredSecrets ?? [])
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            configuration.ProtectedSecrets = NormalizeDictionary(configuration.ProtectedSecrets);
            return configuration;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new();
        }
    }

    private static Dictionary<string, TValue> NormalizeDictionary<TValue>(Dictionary<string, TValue>? source)
    {
        var normalized = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source ?? [])
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
                normalized[pair.Key.Trim()] = pair.Value;
        }
        return normalized;
    }
}
