using Klassenbibliothek.Data;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Services;

public sealed class NoOpPushNotificationDispatcher : IPushNotificationDispatcher
{
    public bool IsConfigured => false;

    public Task RegisterDeviceAsync(string userId, PushDeviceRegistrationRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("Push-Benachrichtigungen sind nur in Sessage Enterprise verfügbar.");

    public Task UnregisterDeviceAsync(string userId, string installationId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendAsync(string userId, string title, string message, Guid listId, Guid? taskId, NotificationEventType eventType, PushNotificationContentMode contentMode, CancellationToken ct = default)
        => Task.CompletedTask;
}
