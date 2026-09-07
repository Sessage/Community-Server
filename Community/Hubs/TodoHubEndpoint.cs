using System.Security.Claims;
using Klassenbibliothek.Hubs;
using Klassenbibliothek.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace TodoSuite.Server.Hubs;

/// <summary>
/// SignalR hub used to notify connected clients that workspace state has changed.
/// Group membership is derived from persisted access instead of trusting arbitrary client group names.
/// </summary>
[Authorize(Policy = "MobileApi")]
public class TodoHubEndpoint(IDbContextFactory<ApplicationDbContext> dbFactory) : Hub
{
    public Task SubscribeToUser(string _)
    {
        var authenticatedUserId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(authenticatedUserId))
            return Task.CompletedTask;

        return Groups.AddToGroupAsync(Context.ConnectionId, TodoHub.UserGroup(authenticatedUserId), Context.ConnectionAborted);
    }

    public async Task SubscribeToList(Guid listId)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return;

        var normalizedUserId = userId.Trim().ToLower();
        await using var db = await dbFactory.CreateDbContextAsync(Context.ConnectionAborted);
        var canRead = await db.TodoLists
            .AsNoTracking()
            .AnyAsync(l =>
                l.Id == listId
                && l.DeletedAt == null
                && (l.OwnerId.ToLower() == normalizedUserId
                    || l.Participants.Any(p => !p.InvitationPending
                        && ((p.UserId ?? "").ToLower() == normalizedUserId
                            || p.Email.ToLower() == normalizedUserId))),
                Context.ConnectionAborted);

        if (!canRead)
            return;

        await Groups.AddToGroupAsync(Context.ConnectionId, TodoHub.ListGroup(listId), Context.ConnectionAborted);
    }

    public Task UnsubscribeFromList(Guid listId)
        => Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            TodoHub.ListGroup(listId),
            Context.ConnectionAborted);

    private string? ResolveUserId()
        => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User?.FindFirstValue("sub");
}
