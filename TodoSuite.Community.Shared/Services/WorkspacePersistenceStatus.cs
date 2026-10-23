namespace Klassenbibliothek.Services;

public enum WorkspacePersistenceState { Confirmed, Local, Unsaved, Transferring, Conflict, Failed }

public sealed record TaskDeletionUndoRequest(Guid ListId, Guid TaskId, string UserId, string UserScope, DateTime ExpiresAtUtc);

/// <summary>Distinguishes a durable device save from confirmation by the server.</summary>
public interface IWorkspacePersistenceStatus
{
    event Action? Changed;
    Task<WorkspacePersistenceState> GetAsync(Guid listId, Guid taskId, CancellationToken ct = default);
}
