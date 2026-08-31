using System.Security.Claims;
using System.Net;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;

namespace TodoSuite.Server.Services;

public sealed class ServerTodoCurrentUserService(AuthenticationStateProvider authStateProvider) : ITodoCurrentUserService
{
    public async Task<TodoCurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user?.Identity?.IsAuthenticated == true)
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                         ?? user.FindFirst("sub")?.Value
                         ?? "gast";

            var displayName = user.Identity?.Name
                              ?? user.FindFirst(ClaimTypes.Email)?.Value
                              ?? userId;

            return new TodoCurrentUser(true, userId, displayName, user.FindFirst(ClaimTypes.Email)?.Value);
        }

        return new TodoCurrentUser(false, "gast", "Gast");
    }
}

public sealed class ServerTodoHubConnectionFactory(
    NavigationManager navigationManager,
    IHttpContextAccessor httpContextAccessor) : ITodoHubConnectionFactory
{
    public Task<HubConnection?> CreateAsync(CancellationToken cancellationToken = default)
    {
        var hubUri = navigationManager.ToAbsoluteUri("/hubs/todo");
        var cookies = CreateCookieContainer(hubUri);

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                if (cookies.Count > 0)
                {
                    options.Cookies = cookies;
                }
            })
            .WithAutomaticReconnect()
            .Build();

        return Task.FromResult<HubConnection?>(connection);
    }

    private CookieContainer CreateCookieContainer(Uri hubUri)
    {
        var container = new CookieContainer();
        var requestCookies = httpContextAccessor.HttpContext?.Request.Cookies;
        if (requestCookies is null || requestCookies.Count == 0)
            return container;

        foreach (var (name, value) in requestCookies)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(value))
                continue;

            container.Add(hubUri, new Cookie(name, value));
        }

        return container;
    }
}
