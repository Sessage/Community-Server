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
