namespace Klassenbibliothek.Hubs;

public static class TodoHub
{
    public const string ListsUpdated = "ListsUpdated";
    public const string TaskCommentsUpdated = "TaskCommentsUpdated";
    public const string TaskAttachmentsUpdated = "TaskAttachmentsUpdated";
    public const string ReminderTriggered = "ReminderTriggered";
    public const string BrowserNotification = "BrowserNotification";

    public const string SubscribeToUser = "SubscribeToUser";
    public const string SubscribeToList = "SubscribeToList";
    public const string UnsubscribeFromList = "UnsubscribeFromList";

    public static string UserGroup(string userId) => $"user:{userId}";
    public static string ListGroup(Guid listId) => $"list:{listId:N}";
}
