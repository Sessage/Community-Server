namespace Klassenbibliothek.Administration;

public sealed record CentralAdministrationPolicySnapshot(
    bool AllowSelfRegistration,
    bool AllowPersonalDataExport,
    bool AllowAccountDeletion);

public interface ICentralAdministrationPolicy
{
    CentralAdministrationPolicySnapshot Current { get; }
}

public interface IAuditEventSink
{
    Task RecordAsync(
        string category,
        string action,
        string actorUserId,
        string? details = null,
        CancellationToken cancellationToken = default);
}

public sealed record EnterpriseOperationsSnapshot(DateTimeOffset? LastWebhookAcceptedAtUtc,
    DateTimeOffset? LastWebhookFailureAtUtc, DateTimeOffset? LastCompleteDirectoryRunAtUtc,
    DateTimeOffset? LastIncompleteDirectoryRunAtUtc, int? LastDirectoryGroupCount, int? LastDirectoryFailedGroupCount);

public sealed record EnterprisePushDiagnostics(bool Configured, DateTimeOffset? LastRegistrationAtUtc,
    DateTimeOffset? LastProviderAcceptanceAtUtc, DateTimeOffset? LastFailureAtUtc, int? LastFailureStatus);

public sealed record EnterpriseDiagnosticsSnapshot(EnterpriseOperationsSnapshot Operations, EnterprisePushDiagnostics? Push);

public interface IEnterpriseOperationsDiagnostics
{
    EnterpriseOperationsSnapshot GetOperations();
    EnterprisePushDiagnostics? GetPush();
}
