namespace Klassenbibliothek.Components.Views;

using Klassenbibliothek.Data;

public sealed class TodoCalendarItem
{
    public Guid? TaskId { get; set; }
    public Guid? ListId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool AllDay { get; set; } = true;
    public string? CardColor { get; set; }
    public TaskCardColorMode CardColorMode { get; set; } = TaskCardColorMode.TopOnly;
    public string? AssigneeUserId { get; set; }
    public string? AssigneeDisplayName { get; set; }
    public string? AssigneeInitials { get; set; }
    public string? AssigneeAvatarColor { get; set; }
    public string? AssigneeProfilePictureUrl { get; set; }
}
