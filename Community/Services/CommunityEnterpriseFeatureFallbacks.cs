using Klassenbibliothek.Data;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Services;

// Community fallbacks preserve the shared service graph without exposing licensed data or
// behavior. Reads return an empty/unavailable projection; writes fail explicitly so a stale or
// malicious client cannot mistake a hidden UI control for server-side feature enforcement.

public sealed class CommunityCustomFieldService : ITodoCustomFieldService
{
    private static InvalidOperationException Unavailable()
        => new("Benutzerdefinierte Felder sind Bestandteil des Enterprise-Moduls Formulare.");

    public Task<TodoCustomFieldDefinitionEntity?> AddFieldAsync(string userId, Guid listId, TodoCustomFieldDefinitionEntity field, CancellationToken cancellationToken = default)
        => Task.FromException<TodoCustomFieldDefinitionEntity?>(Unavailable());
    public Task<TodoCustomFieldDefinitionEntity?> UpdateFieldAsync(string userId, Guid listId, TodoCustomFieldDefinitionEntity field, CancellationToken cancellationToken = default)
        => Task.FromException<TodoCustomFieldDefinitionEntity?>(Unavailable());
    public Task<bool> DeleteFieldAsync(string userId, Guid listId, Guid fieldId, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(Unavailable());
    public Task ReorderFieldsAsync(string userId, Guid listId, IReadOnlyList<Guid> orderedFieldIds, CancellationToken cancellationToken = default)
        => Task.FromException(Unavailable());
}

public sealed class CommunityEmailImportService : IListEmailImportService
{
    public Task<ListEmailImportConfigurationDto?> GetConfigurationAsync(string userId, Guid listId, CancellationToken cancellationToken = default) => Task.FromResult<ListEmailImportConfigurationDto?>(null);
    public Task SaveConfigurationAsync(string userId, Guid listId, ListEmailImportSaveRequest request, CancellationToken cancellationToken = default) => Task.FromException(Unavailable("E-Mail-Import"));
    public Task DeleteConfigurationAsync(string userId, Guid listId, CancellationToken cancellationToken = default) => Task.FromException(Unavailable("E-Mail-Import"));
    public Task<EmailImportConnectionTestResult> TestConnectionAsync(string userId, Guid listId, ListEmailImportSaveRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new EmailImportConnectionTestResult(false, "Enterprise-Modul nicht verfügbar.", []));
    public Task<EmailImportRunResult> ImportListAsync(string userId, Guid listId, CancellationToken cancellationToken = default) => Task.FromResult(new EmailImportRunResult([], "Enterprise-Modul nicht verfügbar."));
    public Task<int> ImportAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    private static InvalidOperationException Unavailable(string name) => new($"{name} ist ein Enterprise-Modul.");
}

public sealed class CommunityFormService : ITodoFormService
{
    public Task<IReadOnlyList<TodoFormEntity>> GetFormsAsync(string userId, Guid listId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TodoFormEntity>>([]);
    public Task<TodoFormEntity?> GetFormForEditAsync(string userId, Guid formId, CancellationToken cancellationToken = default) => Task.FromResult<TodoFormEntity?>(null);
    public Task<TodoFormEntity> CreateFormAsync(string userId, Guid listId, string name, CancellationToken cancellationToken = default) => Task.FromException<TodoFormEntity>(Unavailable());
    public Task<TodoFormEntity?> SaveFormAsync(string userId, TodoFormEntity form, string? plainPassword, CancellationToken cancellationToken = default) => Task.FromException<TodoFormEntity?>(Unavailable());
    public Task<bool> DeleteFormAsync(string userId, Guid formId, CancellationToken cancellationToken = default) => Task.FromException<bool>(Unavailable());
    public Task<TodoFormEntity?> GetFormForSubmissionAsync(string slug, string? userId, CancellationToken cancellationToken = default) => Task.FromResult<TodoFormEntity?>(null);
    public Task<bool> VerifySubmissionPasswordAsync(Guid formId, string? userId, string? password, string? remoteAddress = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<TodoFormSubmitResult> SubmitAsync(TodoFormSubmitRequest request, string? userId, CancellationToken cancellationToken = default) => Task.FromException<TodoFormSubmitResult>(Unavailable());
    private static InvalidOperationException Unavailable() => new("Formulare sind ein Enterprise-Modul.");
}

public sealed class CommunityDashboardService : IDashboardService
{
    public Task<IReadOnlyList<DashboardEntity>> GetDashboardsAsync(string userId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DashboardEntity>>([]);
    public Task<DashboardEntity> CreateDashboardAsync(string userId, DashboardEntity dashboard, CancellationToken ct = default) => Task.FromException<DashboardEntity>(Unavailable());
    public Task<DashboardEntity?> UpdateDashboardAsync(string userId, DashboardEntity dashboard, CancellationToken ct = default) => Task.FromException<DashboardEntity?>(Unavailable());
    public Task<bool> DeleteDashboardAsync(string userId, Guid dashboardId, CancellationToken ct = default) => Task.FromException<bool>(Unavailable());
    public Task<DashboardEntity?> GetOrCreatePortfolioDashboardAsync(string userId, Guid portfolioGroupId, CancellationToken ct = default) => Task.FromResult<DashboardEntity?>(null);
    private static InvalidOperationException Unavailable() => new("Dashboards sind ein Enterprise-Modul.");
}

public sealed class CommunityPortfolioSharingService : IPortfolioSharingService
{
    public Task<bool> CanManageAsync(string requestingUserId, Guid portfolioGroupId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<PortfolioInviteResult> InviteAsync(string requestingUserId, Guid portfolioGroupId, string email, ListRole role, CancellationToken ct = default) => Task.FromResult(new PortfolioInviteResult(false, "Portfolios sind ein Enterprise-Modul.", null));
    public Task<(bool Success, string Message, string? Link)> CreateShareLinkAsync(string requestingUserId, Guid portfolioGroupId, ListRole role, string? comment, CancellationToken ct = default) => Task.FromResult((false, "Portfolios sind ein Enterprise-Modul.", (string?)null));
    public Task<IReadOnlyList<ShareLinkInfo>> GetShareLinksAsync(string requestingUserId, Guid portfolioGroupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ShareLinkInfo>>([]);
    public Task<(bool Success, string Message)> UpdateShareLinkCommentAsync(string requestingUserId, Guid portfolioGroupId, Guid inviteId, string? comment, CancellationToken ct = default) => Task.FromResult((false, "Portfolios sind ein Enterprise-Modul."));
    public Task<(bool Success, string Message)> RevokeShareLinkAsync(string requestingUserId, Guid portfolioGroupId, Guid inviteId, CancellationToken ct = default) => Task.FromResult((false, "Portfolios sind ein Enterprise-Modul."));
    public Task<IReadOnlyList<PortfolioParticipantEntity>> GetParticipantsAsync(string requestingUserId, Guid portfolioGroupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PortfolioParticipantEntity>>([]);
    public Task<(bool Success, string Message)> UpdateParticipantRoleAsync(string requestingUserId, Guid portfolioGroupId, Guid participantId, ListRole role, CancellationToken ct = default) => Task.FromResult((false, "Portfolios sind ein Enterprise-Modul."));
    public Task<(bool Success, string Message)> RemoveParticipantAsync(string requestingUserId, Guid portfolioGroupId, Guid participantId, CancellationToken ct = default) => Task.FromResult((false, "Portfolios sind ein Enterprise-Modul."));
    public Task<(bool Success, string Message)> AcceptAsync(string acceptingUserId, Guid portfolioGroupId, string token, CancellationToken ct = default) => Task.FromResult((false, "Portfolios sind ein Enterprise-Modul."));
}

public sealed class CommunityDirectorySharingService : IDirectorySharingService
{
    public bool IsAvailable => false;
    public Task<IReadOnlyList<DirectoryPrincipal>> SearchAsync(string u, DirectoryShareResourceType t, Guid r, string q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DirectoryPrincipal>>([]);
    public Task<IReadOnlyList<DirectoryShareGrantEntity>> GetGrantsAsync(string u, DirectoryShareResourceType t, Guid r, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DirectoryShareGrantEntity>>([]);
    public Task<(bool Success, string Message)> GrantAsync(string u, DirectoryShareResourceType t, Guid r, DirectoryPrincipal p, ListRole role, CancellationToken ct = default) => Task.FromResult((false, "Verzeichnisfreigaben sind ein Enterprise-Modul."));
    public Task<(bool Success, string Message)> UpdateRoleAsync(string u, Guid g, ListRole role, CancellationToken ct = default) => Task.FromResult((false, "Verzeichnisfreigaben sind ein Enterprise-Modul."));
    public Task<(bool Success, string Message)> RemoveAsync(string u, Guid g, CancellationToken ct = default) => Task.FromResult((false, "Verzeichnisfreigaben sind ein Enterprise-Modul."));
}

public sealed class NoOpDirectoryIdentitySynchronizer : IDirectoryIdentitySynchronizer
{
    public Task SynchronizeAsync(string userId, DirectoryIdentitySnapshot identity, CancellationToken ct = default) => Task.CompletedTask;
}
