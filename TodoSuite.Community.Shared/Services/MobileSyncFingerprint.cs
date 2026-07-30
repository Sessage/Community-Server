using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

/// <summary>
/// Computes opaque, deterministic preconditions for the fields changed by the mobile full-update APIs.
/// Navigation preferences, watchers, comments and attachments use independent endpoints and are excluded.
/// </summary>
public static class MobileSyncFingerprint
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ForTask(TodoTaskEntity task)
        => Hash(new
        {
            task.Id,
            Title = task.Title?.Trim(),
            task.Description,
            task.StartDate,
            task.DueDate,
            task.ReminderAtUtc,
            task.Done,
            task.IsImportant,
            task.Assignee,
            task.Recurrence,
            task.CustomRecurrence,
            task.Column,
            CardColor = task.CardColor?.Trim(),
            task.CardColorMode,
            Members = (task.MemberUserIds?.Count > 0
                    ? task.MemberUserIds
                    : task.Members?.Select(member => member.UserId))
                ?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct()
                .Order()
                .ToArray() ?? [],
            Labels = (task.LabelLinks ?? []).Select(link => link.LabelId).Distinct().Order().ToArray(),
            Steps = (task.Steps ?? []).OrderBy(step => step.Id)
                .Select(step => new { step.Id, Title = step.Title?.Trim(), step.IsCompleted }).ToArray(),
            CustomFields = (task.CustomFieldValues ?? [])
                .Select(value => new { value.FieldId, value.Value })
                .OrderBy(value => value.FieldId)
                .ToArray()
        });

    public static string ForList(TodoListEntity list)
        => Hash(new
        {
            list.Id,
            Name = list.Name?.Trim(),
            list.DefaultView,
            BackgroundColor = list.BackgroundColor?.Trim(),
            Columns = (list.Columns ?? []).Select(value => value?.Trim()).ToArray(),
            Participants = (list.Participants ?? [])
                .Select(participant => new
                {
                    Key = (participant.UserId ?? participant.Email)?.Trim().ToLowerInvariant(),
                    participant.Role,
                    participant.InvitationPending
                })
                .OrderBy(participant => participant.Key)
                .ToArray()
        });

    private static string Hash<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
