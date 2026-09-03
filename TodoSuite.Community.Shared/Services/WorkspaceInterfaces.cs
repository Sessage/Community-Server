using Klassenbibliothek.Data;
using Klassenbibliothek.AutomationPlugins;

namespace Klassenbibliothek.Services;

/// <summary>
/// Signals an optimistic-concurrency conflict at the service boundary. Transport layers
/// translate this into a conflict response; callers must not retry it blindly because that
/// would overwrite a newer edit from another session or device.
/// </summary>
public sealed class WorkspaceConcurrencyException(string message) : Exception(message);

/// <summary>
/// Defines list lifecycle operations shared by Web, Mobile and product modules. Every
/// implementation must authorize <paramref name="userId"/>; UI visibility is not security.
/// </summary>
public interface ITodoListService
{
    Task<IReadOnlyList<TodoListEntity>> GetListsAsync(string userId, CancellationToken cancellationToken = default);
    Task<TodoListEntity?> GetListAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TodoListEntity>> GetNavigationListsAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TodoListEntity>> GetListOptionsAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TodoListEntity>> GetWorkspaceListsAsync(string userId, Guid? currentListId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TodoListEntity>> GetAssignedTaskListsAsync(string userId, IReadOnlyCollection<string> assigneeKeys, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TodoListEntity>> GetTemplatesAsync(string userId, CancellationToken cancellationToken = default);
    Task<TodoListEntity> AddListAsync(string userId, TodoListEntity list, CancellationToken cancellationToken = default);
    Task<TodoListEntity> CreateListFromTemplateAsync(string userId, Guid templateId, string newName, CancellationToken cancellationToken = default);
    Task<TodoListEntity?> UpdateListAsync(string userId, TodoListEntity list, CancellationToken cancellationToken = default);
    Task TransferListOwnershipAsync(string userId, Guid listId, string newOwnerUserIdOrEmail, CancellationToken cancellationToken = default);
    Task<bool> DeleteListAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task RenameListAsync(string userId, Guid listId, string newName, CancellationToken cancellationToken = default);
    Task SetBackgroundColorAsync(string userId, Guid listId, string? backgroundColor, CancellationToken cancellationToken = default);
    Task<bool> SetListWatchingAsync(string userId, Guid listId, bool watching, CancellationToken cancellationToken = default);
}

public record ListEmailImportConfigurationDto(
    bool Enabled,
    int IntervalMinutes,
    string Host,
    int Port,
    bool UseSsl,
    string UserName,
    string FolderName,
    string? TargetColumn,
    bool HasPassword,
    DateTime? LastImportAtUtc,
    string? LastError);

public record ListEmailImportSaveRequest(
    bool Enabled,
    int IntervalMinutes,
    string Host,
    int Port,
    bool UseSsl,
    string UserName,
    string? Password,
    string FolderName,
    string? TargetColumn);

public record EmailImportConnectionTestResult(bool Success, string Message, IReadOnlyList<string> Folders);

public record EmailImportedTaskDto(Guid Id, string Title, string Column, DateTime CreatedAtUtc);

public record EmailImportRunResult(IReadOnlyList<EmailImportedTaskDto> ImportedTasks, string? Error = null)
{
    public int Count => ImportedTasks.Count;
}

/// <summary>Configures and executes mailbox-to-task imports when supported by the product edition.</summary>
public interface IListEmailImportService
{
    Task<ListEmailImportConfigurationDto?> GetConfigurationAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task SaveConfigurationAsync(string userId, Guid listId, ListEmailImportSaveRequest request, CancellationToken cancellationToken = default);
    Task DeleteConfigurationAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task<EmailImportConnectionTestResult> TestConnectionAsync(string userId, Guid listId, ListEmailImportSaveRequest request, CancellationToken cancellationToken = default);
    Task<EmailImportRunResult> ImportListAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task<int> ImportAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Defines task lifecycle, ordering, completion, and assignment operations.</summary>
public interface ITodoTaskService
{
    Task<TodoTaskEntity?> AddTaskAsync(string userId, Guid listId, TodoTaskEntity task, CancellationToken cancellationToken = default);
    Task<TodoTaskEntity?> UpdateTaskAsync(string userId, Guid listId, TodoTaskEntity task, CancellationToken cancellationToken = default);
    Task<bool> DeleteTaskAsync(string userId, Guid listId, Guid taskId, CancellationToken cancellationToken = default);
    Task<TodoTaskEntity?> MoveTaskToListAsync(string userId, Guid fromListId, Guid toListId, Guid taskId, string? desiredTargetColumn = null, CancellationToken cancellationToken = default);
    Task<TodoTaskEntity?> DecideApprovalAsync(string userId, Guid listId, Guid taskId, bool approved, CancellationToken cancellationToken = default);
    Task ReorderListAsync(string userId, Guid listId, IReadOnlyList<Guid> orderedTaskIds, CancellationToken cancellationToken = default);
    Task ReorderKanbanColumnAsync(string userId, Guid listId, string column, IReadOnlyList<Guid> orderedTaskIds, CancellationToken cancellationToken = default);
    Task<bool> SetTaskWatchingAsync(string userId, Guid listId, Guid taskId, bool watching, CancellationToken cancellationToken = default);
}

/// <summary>Defines list automation rules and their execution history.</summary>
public interface ITodoAutomationService
{
    Task<IReadOnlyList<AutomationPluginActionDescriptor>> GetPluginActionsAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TodoAutomationRuleEntity>> GetRulesAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task<TodoAutomationRuleEntity> SaveRuleAsync(string userId, Guid listId, TodoAutomationRuleEntity rule, CancellationToken cancellationToken = default);
    Task<bool> DeleteRuleAsync(string userId, Guid listId, Guid ruleId, CancellationToken cancellationToken = default);
    Task SetRuleEnabledAsync(string userId, Guid listId, Guid ruleId, bool enabled, CancellationToken cancellationToken = default);
    Task ExecuteAsync(TodoAutomationContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the persistent notification inbox and unread count. Realtime and push delivery are
/// projections of this state, not replacements for it.
/// </summary>
public interface INotificationService
{
    Task<IReadOnlyList<BoardNotificationRuleEntity>> GetBoardRulesAsync(string userId, Guid listId, CancellationToken ct = default);
    Task SetBoardRuleAsync(string userId, Guid listId, NotificationEventType eventType, NotificationRecipientGroup groups, CancellationToken ct = default);
    Task<UserNotificationPreferenceEntity> GetUserPreferenceAsync(string userId, CancellationToken ct = default);
    Task SetUserPreferenceAsync(string userId, NotificationDeliveryChannel channel, PushNotificationContentMode? pushContentMode = null, CancellationToken ct = default);
    Task<IReadOnlyList<UserNotificationEntity>> GetLatestAsync(string userId, int take = 20, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default);
    Task MarkReadAsync(string userId, Guid notificationId, CancellationToken ct = default);
    Task MarkAllReadAsync(string userId, CancellationToken ct = default);
    Task DeleteNotificationAsync(string userId, Guid notificationId, CancellationToken ct = default);
    Task DeleteAllNotificationsAsync(string userId, CancellationToken ct = default);
    Task NotifyTaskEventAsync(string actorUserId, Guid listId, Guid? taskId, NotificationEventType eventType, string title, string message, string? assigneeUserId = null, CancellationToken ct = default);
    Task NotifyUserAsync(string recipientUserId, Guid listId, Guid? taskId, NotificationEventType eventType, string title, string message, CancellationToken ct = default);
}

/// <summary>
/// Enterprise extension point for push delivery. Community supplies a safe no-op so shared
/// callers do not have to branch on the installed product.
/// </summary>
public interface IPushNotificationDispatcher
{
    bool IsConfigured { get; }
    Task RegisterDeviceAsync(string userId, PushDeviceRegistrationRequest request, CancellationToken ct = default);
    Task UnregisterDeviceAsync(string userId, string installationId, CancellationToken ct = default);
    Task SendAsync(string userId, string title, string message, Guid listId, Guid? taskId, NotificationEventType eventType, PushNotificationContentMode contentMode, CancellationToken ct = default);
}

/// <summary>Stores presentation preferences that belong to a user rather than to shared list content.</summary>
public interface ITodoListPreferencesService
{
    Task<ListViewPreferenceEntity?> GetListPreferencesAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task SetListPreferencesAsync(string userId, Guid listId, DefaultListView? view, ListSortMode? listSortMode, ListSortMode? kanbanSortMode, CancellationToken cancellationToken = default);
}

/// <summary>Defines board-column lifecycle, ordering, and task movement.</summary>
public interface ITodoColumnService
{
    Task AddColumnAsync(string userId, Guid listId, string columnName, CancellationToken cancellationToken = default);
    Task RenameColumnAsync(string userId, Guid listId, string oldName, string newName, CancellationToken cancellationToken = default);
    Task DeleteColumnAsync(string userId, Guid listId, string columnName, bool deleteTasksInColumn, string? moveTasksToColumn = null, CancellationToken cancellationToken = default);
    Task ReorderColumnsAsync(string userId, Guid listId, IReadOnlyList<string> orderedColumns, CancellationToken cancellationToken = default);
    Task SetDoneColumnAsync(string userId, Guid listId, string columnName, bool isDone, CancellationToken cancellationToken = default);
}

/// <summary>Provides authorized task-attachment metadata and binary transfer operations.</summary>
public interface ITodoAttachmentService
{
    Task<TodoAttachmentEntity?> AddAttachmentAsync(string userId, Guid listId, Guid taskId, string fileName, Stream content, CancellationToken cancellationToken = default, Guid? id = null);
    Task<bool> RemoveAttachmentAsync(string userId, Guid listId, Guid taskId, Guid attachmentId, CancellationToken cancellationToken = default);
    Task<(Stream Stream, string FileName)?> GetAttachmentStreamAsync(string userId, Guid listId, Guid attachmentId, CancellationToken cancellationToken = default);
}

/// <summary>Defines task comment creation, editing, and deletion.</summary>
public interface ITodoCommentService
{
    Task<TodoCommentEntity?> AddCommentAsync(string userId, Guid listId, Guid taskId, string message, CancellationToken cancellationToken = default, Guid? id = null);
    Task<bool> RemoveCommentAsync(string userId, Guid listId, Guid taskId, Guid commentId, CancellationToken cancellationToken = default);
}

/// <summary>Defines ordered checklist-step operations for tasks.</summary>
public interface ITodoStepService
{
    Task<TodoStepEntity?> AddStepAsync(string userId, Guid listId, Guid taskId, string title, CancellationToken cancellationToken = default);
    Task<bool> RemoveStepAsync(string userId, Guid listId, Guid taskId, Guid stepId, CancellationToken cancellationToken = default);
}

/// <summary>Defines list label management and task-label assignments.</summary>
public interface ITodoLabelService
{
    Task<TodoLabelEntity?> AddLabelAsync(string userId, Guid listId, string title, string? backgroundColor, CancellationToken cancellationToken = default, Guid? id = null);
    Task<TodoLabelEntity?> UpdateLabelAsync(string userId, Guid listId, Guid labelId, string title, string? backgroundColor, CancellationToken cancellationToken = default);
    Task<bool> DeleteLabelAsync(string userId, Guid listId, Guid labelId, CancellationToken cancellationToken = default);
}

/// <summary>Defines custom-field schemas and task-specific field values.</summary>
public interface ITodoCustomFieldService
{
    Task<TodoCustomFieldDefinitionEntity?> AddFieldAsync(string userId, Guid listId, TodoCustomFieldDefinitionEntity field, CancellationToken cancellationToken = default);
    Task<TodoCustomFieldDefinitionEntity?> UpdateFieldAsync(string userId, Guid listId, TodoCustomFieldDefinitionEntity field, CancellationToken cancellationToken = default);
    Task<bool> DeleteFieldAsync(string userId, Guid listId, Guid fieldId, CancellationToken cancellationToken = default);
    Task ReorderFieldsAsync(string userId, Guid listId, IReadOnlyList<Guid> orderedFieldIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Manages Enterprise form definitions and submissions while keeping design permissions
/// separate from the deliberately anonymous public submission path.
/// </summary>
public interface ITodoFormService
{
    Task<IReadOnlyList<TodoFormEntity>> GetFormsAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task<TodoFormEntity?> GetFormForEditAsync(string userId, Guid formId, CancellationToken cancellationToken = default);
    Task<TodoFormEntity> CreateFormAsync(string userId, Guid listId, string name, CancellationToken cancellationToken = default);
    Task<TodoFormEntity?> SaveFormAsync(string userId, TodoFormEntity form, string? plainPassword, CancellationToken cancellationToken = default);
    Task<bool> DeleteFormAsync(string userId, Guid formId, CancellationToken cancellationToken = default);
    Task<TodoFormEntity?> GetFormForSubmissionAsync(string slug, string? userId, CancellationToken cancellationToken = default);
    Task<bool> VerifySubmissionPasswordAsync(Guid formId, string? userId, string? password, string? remoteAddress = null, CancellationToken cancellationToken = default);
    Task<TodoFormSubmitResult> SubmitAsync(TodoFormSubmitRequest request, string? userId, CancellationToken cancellationToken = default);
}

/// <summary>Stores a user's preferred order for columns in tabular list views.</summary>
public interface ITodoTableColumnOrderService
{
    Task<IReadOnlyList<string>> SetTableColumnOrderAsync(string userId, Guid listId, IReadOnlyList<string> orderedColumnKeys, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> SetTableHiddenColumnsAsync(string userId, Guid listId, IReadOnlyList<string> hiddenColumnKeys, CancellationToken cancellationToken = default);
}

/// <summary>Defines navigation groups, portfolios, ordering, and collapsed-state persistence.</summary>
public interface ITodoNavigationService
{
    Task<IReadOnlyList<TodoListGroupEntity>> GetListGroupsAsync(string userId, CancellationToken ct = default);
    Task<TodoListGroupEntity> AddListGroupAsync(string userId, string name, bool isPortfolio = false, CancellationToken ct = default, Guid? id = null);
    Task RenameListGroupAsync(string userId, Guid groupId, string newName, CancellationToken ct = default);
    Task SetListGroupPortfolioAsync(string userId, Guid groupId, bool isPortfolio, CancellationToken ct = default);
    Task SetListGroupCollapsedAsync(string userId, Guid groupId, bool isCollapsed, CancellationToken ct = default);
    Task DeleteListGroupAsync(string userId, Guid groupId, bool ungroupLists, CancellationToken ct = default);
    Task ReorderListGroupsAsync(string userId, IReadOnlyList<Guid> orderedGroupIds, CancellationToken ct = default);
    Task ReorderNavigationListsAsync(string userId, Guid? groupId, IReadOnlyList<Guid> orderedListIds, CancellationToken ct = default);
    Task MoveListAsync(string userId, Guid listId, Guid? fromGroupId, Guid? toGroupId, IReadOnlyList<Guid> fromOrderedIds, IReadOnlyList<Guid> toOrderedIds, CancellationToken ct = default);

    /// <summary>
    /// Persistiert die gemischte Reihenfolge von Gruppen und Root-Listen in einem einzigen Aufruf.
    /// orderedDescriptors enthält Einträge der Form "group:&lt;guid&gt;" oder "list:&lt;guid&gt;" in
    /// der gewünschten Reihenfolge. Die globale Position wird direkt als SortOrder / NavigationSortOrder
    /// gespeichert, damit Gruppen und Listen in derselben numerischen Skala verglichen werden können.
    /// </summary>
    Task ReorderMixedNavigationAsync(string userId, IReadOnlyList<string> orderedDescriptors, CancellationToken ct = default);
}

/// <summary>Maintains the members explicitly assigned to a task.</summary>
public interface ITaskMemberService
{
    Task<IReadOnlyList<ListParticipantEntity>> GetEligibleMembersAsync(string callerUserId, Guid listId, CancellationToken ct = default);
    Task SetTaskMembersAsync(string callerUserId, Guid listId, Guid taskId, IReadOnlyCollection<string> memberUserIds, CancellationToken ct = default);
    Task CleanupRemovedListMembersAsync(Guid listId, IReadOnlyCollection<string> removedUserIds, CancellationToken ct = default);
}

public sealed record DeletedTaskTrashItem(
    Guid TaskId,
    Guid ListId,
    string TaskTitle,
    string ListName,
    DateTime DeletedAtUtc);

/// <summary>Provides soft-delete retention, restoration, and permanent task deletion.</summary>
public interface ITodoTrashService
{
    Task<IReadOnlyList<TodoListEntity>> GetDeletedListsAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeletedTaskTrashItem>> GetDeletedTaskEntriesAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TodoTaskEntity>> GetDeletedTasksAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task<bool> RestoreListAsync(string userId, Guid listId, CancellationToken cancellationToken = default);
    Task<bool> RestoreTaskAsync(string userId, Guid listId, Guid taskId, CancellationToken cancellationToken = default);
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

public enum SearchResultKind { List, Task }

public record SearchResultItem(
    SearchResultKind Kind,
    Guid ListId,
    string ListName,
    Guid? TaskId,
    string Title,
    string? MatchField);

/// <summary>Searches workspace entities visible to the current user.</summary>
public interface ISearchService
{
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(string userId, string query, CancellationToken ct = default);
}

/// <summary>Defines configurable dashboard widgets and their calculated data.</summary>
public interface IDashboardService
{
    Task<IReadOnlyList<DashboardEntity>> GetDashboardsAsync(string userId, CancellationToken ct = default);
    Task<DashboardEntity> CreateDashboardAsync(string userId, DashboardEntity dashboard, CancellationToken ct = default);
    Task<DashboardEntity?> UpdateDashboardAsync(string userId, DashboardEntity dashboard, CancellationToken ct = default);
    Task<bool> DeleteDashboardAsync(string userId, Guid dashboardId, CancellationToken ct = default);
    Task<DashboardEntity?> GetOrCreatePortfolioDashboardAsync(string userId, Guid portfolioGroupId, CancellationToken ct = default);
}

public record InviteResult(bool Success, string Message);
public record PortfolioInviteResult(bool Success, string Message, string? Link);

public sealed record DirectoryPrincipal(string Id, DirectoryPrincipalType Type, string DisplayName, string? UserPrincipalName, string? Description = null);
public sealed record DirectoryIdentitySnapshot(string PrincipalId, string UserPrincipalName, string DisplayName, IReadOnlyCollection<string> GroupIds);

/// <summary>Synchronizes external directory identities used by Enterprise sharing rules.</summary>
public interface IDirectoryIdentitySynchronizer
{
    Task SynchronizeAsync(string userId, DirectoryIdentitySnapshot identity, CancellationToken ct = default);
}

/// <summary>Defines list sharing with users and groups from an external directory.</summary>
public interface IDirectorySharingService
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<DirectoryPrincipal>> SearchAsync(string requestingUserId, DirectoryShareResourceType resourceType, Guid resourceId, string query, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryShareGrantEntity>> GetGrantsAsync(string requestingUserId, DirectoryShareResourceType resourceType, Guid resourceId, CancellationToken ct = default);
    Task<(bool Success, string Message)> GrantAsync(string requestingUserId, DirectoryShareResourceType resourceType, Guid resourceId, DirectoryPrincipal principal, ListRole role, CancellationToken ct = default);
    Task<(bool Success, string Message)> UpdateRoleAsync(string requestingUserId, Guid grantId, ListRole role, CancellationToken ct = default);
    Task<(bool Success, string Message)> RemoveAsync(string requestingUserId, Guid grantId, CancellationToken ct = default);
}

/// <summary>Defines portfolio-level invitations and participant administration.</summary>
public interface IPortfolioSharingService
{
    Task<bool> CanManageAsync(string requestingUserId, Guid portfolioGroupId, CancellationToken ct = default);
    Task<PortfolioInviteResult> InviteAsync(string requestingUserId, Guid portfolioGroupId, string email, ListRole role, CancellationToken ct = default);
    Task<(bool Success, string Message, string? Link)> CreateShareLinkAsync(string requestingUserId, Guid portfolioGroupId, ListRole role, string? comment, CancellationToken ct = default);
    Task<IReadOnlyList<ShareLinkInfo>> GetShareLinksAsync(string requestingUserId, Guid portfolioGroupId, CancellationToken ct = default);
    Task<(bool Success, string Message)> UpdateShareLinkCommentAsync(string requestingUserId, Guid portfolioGroupId, Guid inviteId, string? comment, CancellationToken ct = default);
    Task<(bool Success, string Message)> RevokeShareLinkAsync(string requestingUserId, Guid portfolioGroupId, Guid inviteId, CancellationToken ct = default);
    Task<IReadOnlyList<PortfolioParticipantEntity>> GetParticipantsAsync(string requestingUserId, Guid portfolioGroupId, CancellationToken ct = default);
    Task<(bool Success, string Message)> UpdateParticipantRoleAsync(string requestingUserId, Guid portfolioGroupId, Guid participantId, ListRole role, CancellationToken ct = default);
    Task<(bool Success, string Message)> RemoveParticipantAsync(string requestingUserId, Guid portfolioGroupId, Guid participantId, CancellationToken ct = default);
    Task<(bool Success, string Message)> AcceptAsync(string acceptingUserId, Guid portfolioGroupId, string token, CancellationToken ct = default);
}

public record ShareLinkInfo(Guid Id, Guid ListId, string Token, string Link, ListRole Role, string? Comment, bool Revoked, DateTime CreatedAtUtc, DateTime? ExpiresAtUtc, string? QrCodeDataUrl);

public record TodoTaskOpenRequest(TodoTaskEntity Task, string? TabId = null);

/// <summary>Defines list invitations, participant roles, share links, and ownership transfer.</summary>
public interface IListSharingService
{
    Task<(bool Success, string Message, string? Link)> CreateShareLinkAsync(string requestingUserId, Guid listId, ListRole role, string? comment);
    Task<InviteResult> InviteByEmailAsync(string requestingUserId, Guid listId, string email, string displayName, ListRole role);
    Task<(bool Success, string Message)> AcceptShareLinkAsync(string acceptingUserId, Guid listId, string token);
    Task<IReadOnlyList<ShareLinkInfo>> GetShareLinksAsync(string requestingUserId, Guid listId);
    Task<(bool Success, string Message)> UpdateShareLinkCommentAsync(string requestingUserId, Guid listId, Guid inviteId, string? comment);
    Task<(bool Success, string Message)> RevokeShareLinkAsync(string requestingUserId, Guid listId, Guid inviteId);
    Task<(bool Success, string Message)> UpdateParticipantRoleAsync(string requestingUserId, Guid listId, Guid participantId, ListRole role);
    Task<(bool Success, string Message)> RemovePendingInvitationAsync(string requestingUserId, Guid listId, Guid participantId);
}
