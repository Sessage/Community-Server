using Klassenbibliothek.Administration;

namespace TodoSuite.Server.Services;

public sealed class CommunityCentralAdministrationPolicy(AdminSettingsService settings)
    : ICentralAdministrationPolicy
{
    public CentralAdministrationPolicySnapshot Current
        => new(settings.AllowSelfRegistration, true, true);
}

public sealed class NoOpAuditEventSink : IAuditEventSink
{
    public Task RecordAsync(string category, string action, string actorUserId, string? details = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
