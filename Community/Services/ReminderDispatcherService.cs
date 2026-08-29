using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using Klassenbibliothek.Data;
using Klassenbibliothek.Hubs;
using Klassenbibliothek.Localization;
using Klassenbibliothek.Services;
using TodoSuite.Server.Services.Sharing;

namespace TodoSuite.Server.Services;

/// <summary>
/// Periodically claims due reminders and creates their durable inbox entries and optional
/// deliveries. The persisted sent timestamp is the idempotency boundary across restarts and
/// multiple application instances.
/// </summary>
public sealed class ReminderDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<TodoHubEndpoint> _hub;
    private readonly string _appBaseUrl;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<ReminderDispatcherService> _logger;
    private readonly IPushNotificationDispatcher _push;

    public ReminderDispatcherService(
        IServiceScopeFactory scopeFactory,
        IHubContext<TodoHubEndpoint> hub,
        IOptions<SmtpOptions> smtpOptions,
        IStringLocalizer<SharedResource> localizer,
        ILogger<ReminderDispatcherService> logger,
        IPushNotificationDispatcher push)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _appBaseUrl = smtpOptions.Value.AppBaseUrl.TrimEnd('/');
        _localizer = localizer;
        _logger = logger;
        _push = push;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not let the initial reminder query compete with Kestrel reaching its ready
        // state. This short delay only affects the first pass after process startup; the
        // regular dispatch interval and reminder semantics remain unchanged.
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reminder-Dispatcher-Lauf ist fehlgeschlagen.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    internal async Task DispatchOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // One scope per pass gives EF and delivery services a bounded lifetime and prevents a
        // long-running background singleton from retaining a DbContext indefinitely.
        using var scope = _scopeFactory.CreateScope();

        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var due = await db.TodoTasks
            .Include(t => t.List!).ThenInclude(l => l.Participants)
            .Where(t => !t.Done
                        && t.ReminderAtUtc != null
                        && t.ReminderAtUtc <= now
                        && t.ReminderSentAtUtc == null)
            .OrderBy(t => t.ReminderAtUtc)
            .Take(50)
            .ToListAsync(ct);

        var pendingLiveNotifications = new List<(string UserId, string Title, string Message, Guid TaskId)>();
        var pendingPushNotifications = new List<(string UserId, string Title, string Message, Guid ListId, Guid TaskId, PushNotificationContentMode Mode)>();

        foreach (var task in due)
        {
            var (recipientUserId, recipientEmail) = ResolveRecipient(task);
            var title = string.Format(_localizer["Email_Reminder_Subject"].Value, task.Title);
            var message = task.Description ?? _localizer["Email_Reminder_TaskDue"].Value;
            var delivered = false;
            var preference = string.IsNullOrWhiteSpace(recipientUserId)
                ? null
                : await db.UserNotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == recipientUserId, ct);
            var channel = preference?.Channel ?? NotificationDeliveryChannel.Browser;

            if (!string.IsNullOrWhiteSpace(recipientUserId)
                && (channel & (NotificationDeliveryChannel.Browser | NotificationDeliveryChannel.Push)) != 0)
            {
                db.UserNotifications.Add(new UserNotificationEntity
                {
                    UserId = recipientUserId,
                    ListId = task.ListId,
                    TaskId = task.Id,
                    EventType = NotificationEventType.TaskUpdated,
                    Title = title,
                    Message = message,
                    CreatedAtUtc = now
                });

                delivered = true;
                if ((channel & NotificationDeliveryChannel.Browser) != 0)
                    pendingLiveNotifications.Add((recipientUserId, title, message, task.Id));
                if ((channel & NotificationDeliveryChannel.Push) != 0)
                    pendingPushNotifications.Add((recipientUserId, title, message, task.ListId, task.Id,
                        preference?.PushContentMode ?? PushNotificationContentMode.Anonymous));
            }

            // E-Mail: nur als geliefert werten, wenn der Versand wirklich klappt.
            if (!string.IsNullOrWhiteSpace(recipientEmail)
                && (string.IsNullOrWhiteSpace(recipientUserId) || (channel & NotificationDeliveryChannel.Email) != 0))
            {
                try
                {
                    var body = BuildReminderEmail(task);
                    await email.SendEmailAsync(recipientEmail, title, body);
                    delivered = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reminder-E-Mail konnte nicht gesendet werden. TaskId={TaskId}, Recipient={RecipientEmail}", task.Id, recipientEmail);
                    // Retry im nächsten Dispatcher-Lauf, falls kein anderer Kanal geliefert wurde.
                }
            }

            if (delivered)
                task.ReminderSentAtUtc = now;
        }

        if (due.Count > 0)
            await db.SaveChangesAsync(ct);

        // Erst nach dem Commit benachrichtigen: Die Clients laden beim Signal sofort
        // Liste und Ungelesen-Zähler. Vor dem Commit konnten sie sporadisch noch 0 sehen.
        foreach (var notification in pendingLiveNotifications)
        {
            try
            {
                await _hub.Clients.Group(TodoHub.UserGroup(notification.UserId))
                    .SendAsync(TodoHub.ReminderTriggered,
                        notification.Title,
                        notification.Message,
                        notification.TaskId,
                        cancellationToken: ct);

                await _hub.Clients.Group(TodoHub.UserGroup(notification.UserId))
                    .SendAsync(TodoHub.BrowserNotification, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Reminder-SignalR-Benachrichtigung konnte nicht gesendet werden. TaskId={TaskId}, UserId={UserId}", notification.TaskId, notification.UserId);
                // In-App-Postfach ist bereits persistiert; SignalR ist nur Live-Refresh.
            }
        }

        foreach (var notification in pendingPushNotifications)
        {
            await _push.SendAsync(
                notification.UserId,
                notification.Title,
                notification.Message,
                notification.ListId,
                notification.TaskId,
                NotificationEventType.TaskUpdated,
                notification.Mode,
                ct);
        }
    }

    private string BuildReminderEmail(TodoTaskEntity task)
    {
        var titleEncoded = WebUtility.HtmlEncode(task.Title);
        var descEncoded = WebUtility.HtmlEncode(task.Description ?? "");
        var listUrl = string.IsNullOrWhiteSpace(_appBaseUrl)
            ? $"/list/{task.ListId}"
            : $"{_appBaseUrl}/list/{task.ListId}";
        var listUrlEncoded = WebUtility.HtmlEncode(listUrl);
        var heading = WebUtility.HtmlEncode(string.Format(_localizer["Email_Reminder_Subject"].Value, task.Title));
        var goToList = WebUtility.HtmlEncode(_localizer["Email_Reminder_GoToList"].Value);
        var linkFallback = WebUtility.HtmlEncode(_localizer["Email_Reminder_LinkFallback"].Value);

        return $@"<!doctype html>
<html lang=""de"">
<head>
  <meta charset=""utf-8"" />
</head>
<body style=""font-family:Segoe UI, Arial, sans-serif; background:#f8fafc; padding:24px;"">
  <div style=""max-width:560px; margin:0 auto; background:#ffffff; border:1px solid #e2e8f0; border-radius:16px; padding:20px;"">
    <h2 style=""margin:0 0 12px 0; font-size:18px; color:#0f172a;"">{heading}</h2>
    {(string.IsNullOrWhiteSpace(task.Description) ? "" : $@"<p style=""margin:0 0 16px 0; color:#334155; font-size:14px;"">{descEncoded}</p>")}
    <p style=""margin:0 0 18px 0;"">
      <a href=""{listUrlEncoded}""
         style=""display:inline-block; background:#2563eb; color:#ffffff; text-decoration:none; padding:10px 14px; border-radius:12px; font-weight:600;"">
        {goToList}
      </a>
    </p>
    <p style=""margin:0 0 12px 0; color:#64748b; font-size:12px;"">
      {linkFallback}
    </p>
    <p style=""margin:0 0 16px 0; font-size:12px; color:#0f172a; word-break:break-all;"">
      {listUrlEncoded}
    </p>
  </div>
</body>
</html>";
    }

    private static (string? UserId, string? Email) ResolveRecipient(TodoTaskEntity task)
    {
        var list = task.List;

        // 1) Assignee (UserId)
        if (!string.IsNullOrWhiteSpace(task.Assignee) && list is not null)
        {
            var p = list.Participants.FirstOrDefault(x =>
                string.Equals(x.UserId, task.Assignee, StringComparison.OrdinalIgnoreCase));

            if (p is not null)
                return (p.UserId, string.IsNullOrWhiteSpace(p.Email) ? null : p.Email);
        }

        // 2) Owner fallback
        if (list is not null)
        {
            var owner = list.Participants.FirstOrDefault(x =>
                string.Equals(x.UserId, list.OwnerId, StringComparison.OrdinalIgnoreCase));

            if (owner is not null)
                return (owner.UserId, string.IsNullOrWhiteSpace(owner.Email) ? null : owner.Email);

            return (list.OwnerId, null);
        }

        return (null, null);
    }
}
