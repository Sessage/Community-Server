using System.Net;
using Klassenbibliothek.Data;
using Klassenbibliothek.Hubs;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TodoSuite.Server.Services.Sharing;
using Klassenbibliothek.Features;

namespace TodoSuite.Server.Services;

/// <summary>
/// Applies board rules and user preferences to create the durable notification inbox, then
/// fans out optional SignalR, email and Enterprise push delivery. Persistence remains the
/// source for unread badges even when an auxiliary channel fails.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private static readonly NotificationEventType[] DefaultEvents =
    [
        NotificationEventType.TaskCreated,
        NotificationEventType.TaskUpdated,
        NotificationEventType.TaskAssigned,
        NotificationEventType.TaskCompleted,
        NotificationEventType.TaskReopened,
        NotificationEventType.TaskDeleted,
        NotificationEventType.TaskMoved,
        NotificationEventType.CommentAdded,
        NotificationEventType.CommentDeleted,
        NotificationEventType.AttachmentAdded,
        NotificationEventType.AttachmentDeleted,
        NotificationEventType.ApprovalGranted,
        NotificationEventType.ApprovalRejected
    ];

    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IHubContext<TodoHubEndpoint> _hubContext;
    private readonly IEmailSender _emailSender;
    private readonly SmtpOptions _smtpOptions;
    private readonly IPushNotificationDispatcher _push;
    private readonly IProductFeatureCatalog _features;

    public NotificationService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IEmailSender emailSender,
        IOptions<SmtpOptions> smtpOptions,
        IPushNotificationDispatcher push,
        IProductFeatureCatalog features)
    {
        _dbContextFactory = dbContextFactory;
        _hubContext = hubContext;
        _emailSender = emailSender;
        _smtpOptions = smtpOptions.Value;
        _push = push;
        _features = features;
    }

    public async Task<IReadOnlyList<BoardNotificationRuleEntity>> GetBoardRulesAsync(string userId, Guid listId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.NotificationRules)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, ct);

        if (list is null || !CanRead(userId, list))
            return [];

        await EnsureDefaultRulesAsync(db, listId, ct);

        return await db.BoardNotificationRules
            .Where(r => r.ListId == listId)
            .OrderBy(r => r.EventType)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task SetBoardRuleAsync(string userId, Guid listId, NotificationEventType eventType, NotificationRecipientGroup groups, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, ct);

        if (list is null)
            throw new InvalidOperationException("Board wurde nicht gefunden.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException("Nur Admins können Benachrichtigungen für dieses Board ändern.");

        var rule = await db.BoardNotificationRules.FirstOrDefaultAsync(r => r.ListId == listId && r.EventType == eventType, ct);
        if (rule is null)
        {
            rule = new BoardNotificationRuleEntity { ListId = listId, EventType = eventType };
            db.BoardNotificationRules.Add(rule);
        }

        rule.RecipientGroups = groups;
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserNotificationPreferenceEntity> GetUserPreferenceAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var pref = await db.UserNotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return pref ?? new UserNotificationPreferenceEntity { UserId = userId, Channel = NotificationDeliveryChannel.Browser };
    }

    public async Task SetUserPreferenceAsync(string userId, NotificationDeliveryChannel channel, PushNotificationContentMode? pushContentMode = null, CancellationToken ct = default)
    {
        if ((channel & ~NotificationDeliveryChannel.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if (pushContentMode.HasValue && !Enum.IsDefined(pushContentMode.Value))
            throw new ArgumentOutOfRangeException(nameof(pushContentMode));
        if ((channel & NotificationDeliveryChannel.Push) != 0
            && !_features.IsEnabled(ProductFeatureIds.PushNotifications))
            throw new InvalidOperationException("Push-Benachrichtigungen sind nur mit einer dafür lizenzierten Enterprise-Installation verfügbar.");

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var pref = await db.UserNotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (pref is null)
        {
            pref = new UserNotificationPreferenceEntity { UserId = userId };
            db.UserNotificationPreferences.Add(pref);
        }

        pref.Channel = channel;
        if (pushContentMode.HasValue)
            pref.PushContentMode = pushContentMode.Value;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UserNotificationEntity>> GetLatestAsync(string userId, int take = 20, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.UserNotifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 100))
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.UserNotifications.CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, ct);
    }

    public async Task MarkAllReadAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await db.UserNotifications
            .Where(n => n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, DateTime.UtcNow), ct);

        await _hubContext.Clients.Group(TodoHub.UserGroup(userId)).SendAsync(TodoHub.BrowserNotification, ct);
    }

    public async Task DeleteNotificationAsync(string userId, Guid notificationId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await db.UserNotifications
            .Where(n => n.UserId == userId && n.Id == notificationId)
            .ExecuteDeleteAsync(ct);

        await _hubContext.Clients.Group(TodoHub.UserGroup(userId)).SendAsync(TodoHub.BrowserNotification, ct);
    }

    public async Task DeleteAllNotificationsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await db.UserNotifications
            .Where(n => n.UserId == userId)
            .ExecuteDeleteAsync(ct);

        await _hubContext.Clients.Group(TodoHub.UserGroup(userId)).SendAsync(TodoHub.BrowserNotification, ct);
    }

    public async Task NotifyTaskEventAsync(string actorUserId, Guid listId, Guid? taskId, NotificationEventType eventType, string title, string message, string? assigneeUserId = null, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.Watchers)
            .Include(l => l.NotificationRules)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, ct);

        if (list is null)
            return;

        await EnsureDefaultRulesAsync(db, listId, ct);

        var rule = await db.BoardNotificationRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ListId == listId && r.EventType == eventType, ct);

        var groups = rule?.RecipientGroups ?? DefaultGroups(eventType);
        if (groups == NotificationRecipientGroup.None)
            return;

        var recipients = await ResolveRecipientsAsync(db, list, taskId, groups, assigneeUserId, actorUserId, ct);
        recipients.RemoveWhere(id => string.Equals(id, actorUserId, StringComparison.OrdinalIgnoreCase));

        if (recipients.Count == 0)
            return;

        var configuredPreferences = await db.UserNotificationPreferences
            .Where(p => recipients.Contains(p.UserId))
            .AsNoTracking()
            .ToListAsync(ct);
        var prefs = configuredPreferences.ToDictionary(p => p.UserId, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        foreach (var recipient in recipients)
        {
            var preference = prefs.GetValueOrDefault(recipient);
            var channel = preference?.Channel ?? NotificationDeliveryChannel.Browser;
            if ((channel & (NotificationDeliveryChannel.Browser | NotificationDeliveryChannel.Push)) != 0)
            {
                db.UserNotifications.Add(new UserNotificationEntity
                {
                    UserId = recipient,
                    ListId = listId,
                    TaskId = taskId,
                    EventType = eventType,
                    Title = title,
                    Message = message,
                    CreatedAtUtc = now
                });
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var recipient in recipients)
        {
            var preference = prefs.GetValueOrDefault(recipient);
            var channel = preference?.Channel ?? NotificationDeliveryChannel.Browser;
            if ((channel & NotificationDeliveryChannel.Browser) != 0)
                await _hubContext.Clients.Group(TodoHub.UserGroup(recipient)).SendAsync(TodoHub.BrowserNotification, ct);

            if ((channel & NotificationDeliveryChannel.Email) != 0)
                await TrySendEmailAsync(db, recipient, title, message, listId, taskId, ct);

            if ((channel & NotificationDeliveryChannel.Push) != 0)
            {
                await _push.SendAsync(
                    recipient,
                    title,
                    message,
                    listId,
                    taskId,
                    eventType,
                    preference?.PushContentMode ?? PushNotificationContentMode.Anonymous,
                    ct);
            }
        }
    }

    /// <summary>
    /// Delivers a mandatory workflow notification to exactly one user. Approval requests must
    /// not depend on optional board recipient rules or on the actor being a different person.
    /// </summary>
    public async Task NotifyUserAsync(
        string recipientUserId,
        Guid listId,
        Guid? taskId,
        NotificationEventType eventType,
        string title,
        string message,
        CancellationToken ct = default)
    {
        var recipient = recipientUserId.Trim();
        if (recipient.Length == 0)
            return;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var canRead = await db.TodoLists
            .Where(l => l.Id == listId && l.DeletedAt == null)
            .AnyAsync(l => l.OwnerId == recipient
                || l.Participants.Any(p => !p.InvitationPending && p.UserId == recipient), ct);
        if (!canRead)
            return;

        db.UserNotifications.Add(new UserNotificationEntity
        {
            UserId = recipient,
            ListId = listId,
            TaskId = taskId,
            EventType = eventType,
            Title = title,
            Message = message,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        await _hubContext.Clients.Group(TodoHub.UserGroup(recipient)).SendAsync(TodoHub.BrowserNotification, ct);
        await TrySendEmailAsync(db, recipient, title, message, listId, taskId, ct);

        var preference = await db.UserNotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == recipient, ct);
        if (preference is not null && (preference.Channel & NotificationDeliveryChannel.Push) != 0)
        {
            await _push.SendAsync(
                recipient, title, message, listId, taskId, eventType,
                preference.PushContentMode, ct);
        }
    }

    private async Task<HashSet<string>> ResolveRecipientsAsync(
        ApplicationDbContext db,
        TodoListEntity list,
        Guid? taskId,
        NotificationRecipientGroup groups,
        string? assigneeUserId,
        string actorUserId,
        CancellationToken ct)
    {
        var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if ((groups & NotificationRecipientGroup.Admins) != 0)
        {
            recipients.Add(list.OwnerId);
            foreach (var p in list.Participants.Where(p => p.Role == ListRole.Admin && !string.IsNullOrWhiteSpace(p.UserId)))
                recipients.Add(p.UserId!);
        }

        if ((groups & NotificationRecipientGroup.Members) != 0)
        {
            recipients.Add(list.OwnerId);
            foreach (var p in list.Participants.Where(p => p.Role != ListRole.Observer && !string.IsNullOrWhiteSpace(p.UserId)))
                recipients.Add(p.UserId!);
        }

        if ((groups & NotificationRecipientGroup.ProjectWatchers) != 0)
        {
            foreach (var w in list.Watchers.Where(w => !string.IsNullOrWhiteSpace(w.UserId)))
                recipients.Add(w.UserId);
        }

        if ((groups & NotificationRecipientGroup.TaskWatchers) != 0 && taskId.HasValue)
        {
            var taskWatchers = await db.TodoTaskWatchers
                .Where(w => w.TaskId == taskId.Value)
                .Select(w => w.UserId)
                .ToListAsync(ct);

            foreach (var userId in taskWatchers)
                recipients.Add(userId);
        }

        if ((groups & NotificationRecipientGroup.Assignee) != 0 && !string.IsNullOrWhiteSpace(assigneeUserId))
            recipients.Add(assigneeUserId.Trim());

        if ((groups & NotificationRecipientGroup.Author) != 0 && !string.IsNullOrWhiteSpace(actorUserId))
            recipients.Add(actorUserId.Trim());

        return recipients;
    }

    private async Task TrySendEmailAsync(ApplicationDbContext db, string userId, string title, string message, Guid listId, Guid? taskId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_smtpOptions.Host) || string.IsNullOrWhiteSpace(_smtpOptions.FromAddress))
            return;

        var email = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(email))
            return;

        var appBase = (_smtpOptions.AppBaseUrl ?? "").TrimEnd('/');
        var path = taskId.HasValue ? $"/list/{listId}?taskId={taskId}" : $"/list/{listId}";
        var url = string.IsNullOrWhiteSpace(appBase) ? path : $"{appBase}{path}";

        var body = $"""
            <!doctype html>
            <html lang="de">
            <body style="font-family:Segoe UI, Arial, sans-serif; background:#f8fafc; padding:24px;">
              <div style="max-width:560px; margin:0 auto; background:#fff; border:1px solid #e2e8f0; border-radius:12px; padding:20px;">
                <h2 style="font-size:18px; color:#0f172a;">{WebUtility.HtmlEncode(title)}</h2>
                <p style="font-size:14px; color:#334155;">{WebUtility.HtmlEncode(message)}</p>
                <p><a href="{WebUtility.HtmlEncode(url)}">In Sessage öffnen</a></p>
              </div>
            </body>
            </html>
            """;

        try { await _emailSender.SendEmailAsync(email, title, body); }
        catch { }
    }

    private static NotificationRecipientGroup DefaultGroups(NotificationEventType eventType)
        => eventType switch
        {
            NotificationEventType.TaskAssigned => NotificationRecipientGroup.Assignee | NotificationRecipientGroup.ProjectWatchers | NotificationRecipientGroup.TaskWatchers,
            NotificationEventType.CommentAdded or NotificationEventType.AttachmentAdded => NotificationRecipientGroup.ProjectWatchers | NotificationRecipientGroup.TaskWatchers,
            _ => NotificationRecipientGroup.ProjectWatchers | NotificationRecipientGroup.TaskWatchers
        };

    private static async Task EnsureDefaultRulesAsync(ApplicationDbContext db, Guid listId, CancellationToken ct)
    {
        var existing = await db.BoardNotificationRules
            .Where(r => r.ListId == listId)
            .Select(r => r.EventType)
            .ToListAsync(ct);

        var set = existing.ToHashSet();
        foreach (var eventType in DefaultEvents)
        {
            if (!set.Contains(eventType))
            {
                db.BoardNotificationRules.Add(new BoardNotificationRuleEntity
                {
                    ListId = listId,
                    EventType = eventType,
                    RecipientGroups = DefaultGroups(eventType)
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool CanRead(string userId, TodoListEntity list)
        => EqualsUserKey(list.OwnerId, userId)
           || list.Participants.Any(p => !p.InvitationPending && (EqualsUserKey(p.Email, userId) || EqualsUserKey(p.UserId, userId)));

    private static bool CanAdmin(string userId, TodoListEntity list)
        => EqualsUserKey(list.OwnerId, userId)
           || list.Participants.Any(p => !p.InvitationPending && (EqualsUserKey(p.UserId, userId) || EqualsUserKey(p.Email, userId)) && p.Role == ListRole.Admin);

    private static bool EqualsUserKey(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
