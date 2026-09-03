using Klassenbibliothek.Administration;

namespace TodoSuite.Server.Services;

/// <summary>Projects Community settings into the administration policy consumed by shared authentication flows.</summary>
public sealed class CommunityCentralAdministrationPolicy(AdminSettingsService settings)
    : ICentralAdministrationPolicy
{
    public CentralAdministrationPolicySnapshot Current
        => new(settings.AllowSelfRegistration, true, true);
}

/// <summary>Community audit sink used when durable Enterprise audit logging is unavailable.</summary>
public sealed class NoOpAuditEventSink : IAuditEventSink
{
    public Task RecordAsync(string category, string action, string actorUserId, string? details = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
