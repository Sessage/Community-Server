using Microsoft.AspNetCore.SignalR.Client;

namespace Klassenbibliothek.Services;

public sealed record TodoCurrentUser(bool IsAuthenticated, string UserId, string DisplayName, string? Email = null);

public interface ITodoCurrentUserService
{
    Task<TodoCurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}

public interface ITodoHubConnectionFactory
{
    Task<HubConnection?> CreateAsync(CancellationToken cancellationToken = default);
}
