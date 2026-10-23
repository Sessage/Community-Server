namespace Klassenbibliothek.Services;

/// <summary>Content-free diagnostic view of a durable mail import attempt.</summary>
public sealed record EmailImportMessageStatus(Guid Id, uint MessageUid, int AttemptCount, Guid TaskId,
    DateTime? TaskCreatedAutomationCompletedAtUtc, DateTime? AutomationCompletedAtUtc, string? LastError);
