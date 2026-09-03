using Microsoft.AspNetCore.SignalR.Client;

namespace Klassenbibliothek.Services;

public sealed record TodoCurrentUser(bool IsAuthenticated, string UserId, string DisplayName, string? Email = null);

/// <summary>Resolves the authenticated user without tying shared components to a hosting platform.</summary>
public interface ITodoCurrentUserService
{
    Task<TodoCurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates a platform-appropriate real-time workspace connection for the authenticated user.</summary>
public interface ITodoHubConnectionFactory
{
    Task<HubConnection?> CreateAsync(CancellationToken cancellationToken = default);
}
