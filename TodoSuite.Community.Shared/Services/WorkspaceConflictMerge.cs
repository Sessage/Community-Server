using System.Text.Json.Nodes;

namespace Klassenbibliothek.Services;

public sealed record WorkspaceFieldDifference(string Field, string LocalValue, string ServerValue);

/// <summary>Explicit full-draft comparison. Only fields owned by the update endpoint can be selected.</summary>
public static class WorkspaceConflictMerge
{
    private static readonly string[] TaskFields = ["title", "description", "startDate", "dueDate", "reminderAtUtc", "done",
        "isImportant", "assignee", "recurrence", "customRecurrence", "column", "cardColor", "cardColorMode",
        "memberUserIds", "labelLinks", "steps", "customFieldValues", "listSortOrder", "kanbanSortOrder"];
    private static readonly string[] ListFields = ["name", "defaultView", "backgroundColor", "columns"];

    public static IReadOnlyList<WorkspaceFieldDifference> Compare(string localJson, string serverJson, bool task)
    {
        var local = JsonNode.Parse(localJson)!.AsObject();
        var server = JsonNode.Parse(serverJson)!.AsObject();
        return (task ? TaskFields : ListFields).Where(field => !JsonNode.DeepEquals(local[field], server[field]))
            .Select(field => new WorkspaceFieldDifference(field, local[field]?.ToJsonString() ?? "null", server[field]?.ToJsonString() ?? "null")).ToArray();
    }

    public static string Merge(string localJson, string serverJson, bool task, IReadOnlySet<string> localFields)
    {
        var local = JsonNode.Parse(localJson)!.AsObject();
        var merged = JsonNode.Parse(serverJson)!.AsObject();
        foreach (var field in task ? TaskFields : ListFields)
            if (localFields.Contains(field)) merged[field] = local[field]?.DeepClone();
        // Identity, authorization context and concurrency preconditions always come from
        // the freshly loaded server entity, never from the selected old draft.
        return merged.ToJsonString();
    }
}
