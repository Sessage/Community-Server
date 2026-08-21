using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

public enum NotificationEventType
{
    TaskCreated = 0,
    TaskUpdated = 1,
    TaskAssigned = 2,
    TaskCompleted = 3,
    TaskReopened = 4,
    TaskDeleted = 5,
    TaskMoved = 6,
    CommentAdded = 7,
    CommentDeleted = 8,
    AttachmentAdded = 9,
    AttachmentDeleted = 10,
    ApprovalRequested = 11,
    ApprovalGranted = 12,
    ApprovalRejected = 13
}

[Flags]
public enum NotificationRecipientGroup
{
    None = 0,
    Admins = 1,
    Members = 2,
    ProjectWatchers = 4,
    TaskWatchers = 8,
    Assignee = 16,
    Author = 32
}

[Flags]
public enum NotificationDeliveryChannel
{
    None = 0,
    Email = 1,
    Browser = 2,
    Push = 4,
    Both = Email | Browser,
    All = Email | Browser | Push
}

public enum PushNotificationContentMode
{
    Anonymous = 0,
    Detailed = 1
}

public enum PushPlatform
{
    FcmV1 = 0,
    Apns = 1,
    Wns = 2
}

public sealed record PushDisplayContent(string Title, string Body);

public static class PushNotificationContentPolicy
{
    public static PushDisplayContent Create(PushNotificationContentMode mode, string? title, string? message)
        => mode == PushNotificationContentMode.Detailed
            ? new PushDisplayContent(Limit(title, 160), Limit(message, 500))
            : new PushDisplayContent("Sessage", "Eine Benachrichtigung von Sessage ist eingegangen");

    private static string Limit(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

public class TodoListWatcherEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(List))]
    public Guid ListId { get; set; }

    public TodoListEntity? List { get; set; }
}

public class TodoTaskWatcherEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(Task))]
    public Guid TaskId { get; set; }

    public TodoTaskEntity? Task { get; set; }
}

public class BoardNotificationRuleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public NotificationEventType EventType { get; set; }

    public NotificationRecipientGroup RecipientGroups { get; set; } = NotificationRecipientGroup.ProjectWatchers | NotificationRecipientGroup.TaskWatchers;

    [ForeignKey(nameof(List))]
    public Guid ListId { get; set; }

    public TodoListEntity? List { get; set; }
}

public class UserNotificationPreferenceEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    public NotificationDeliveryChannel Channel { get; set; } = NotificationDeliveryChannel.Browser;

    public PushNotificationContentMode PushContentMode { get; set; } = PushNotificationContentMode.Anonymous;
}

public sealed record PushDeviceRegistrationRequest(
    string InstallationId,
    PushPlatform Platform,
    string PushChannel,
    string? AppVersion = null);

public sealed record PushDeviceRegistrationStatus(bool Enabled, bool Configured, string? Message = null);

public class UserNotificationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public Guid ListId { get; set; }

    public Guid? TaskId { get; set; }

    public NotificationEventType EventType { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ReadAtUtc { get; set; }
}
