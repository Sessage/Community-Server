using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;

namespace TodoSuite.Server.Services;

/// <summary>
/// Implementiert die Verwaltung der Listen-Ansichtspräferenzen.
/// </summary>
public class TodoListPreferencesService : TodoWorkspaceServiceBase, ITodoListPreferencesService
{
    /// <summary>
    /// Erstellt eine neue Instanz der Preferences-Verwaltung.
    /// </summary>
    public TodoListPreferencesService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
    }

    /// <inheritdoc />
    public async Task<ListViewPreferenceEntity?> GetListPreferencesAsync(
        string userId,
        Guid listId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken);

        if (list is null) return null;

        if (!CanRead(userId, list))
            throw new UnauthorizedAccessException(
                $"Preferences können nicht gelesen werden (Liste='{list.Name}', User='{userId}').");

        return await db.ListViewPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ListId == listId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetListPreferencesAsync(
        string userId,
        Guid listId,
        DefaultListView? view,
        ListSortMode? listSortMode,
        ListSortMode? kanbanSortMode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken);

        if (list is null) return;

        if (!CanRead(userId, list))
            throw new UnauthorizedAccessException(
                $"Preferences können nicht gespeichert werden (Liste='{list.Name}', User='{userId}').");

        var pref = await db.ListViewPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ListId == listId, cancellationToken);

        if (pref is null)
        {
            pref = new ListViewPreferenceEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ListId = listId,
                LastView = view ?? DefaultListView.Liste,
                ListSortMode = listSortMode ?? ListSortMode.Custom,
                KanbanSortMode = kanbanSortMode ?? ListSortMode.Custom,
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.ListViewPreferences.Add(pref);
        }
        else
        {
            if (view.HasValue) pref.LastView = view.Value;
            if (listSortMode.HasValue) pref.ListSortMode = listSortMode.Value;
            if (kanbanSortMode.HasValue) pref.KanbanSortMode = kanbanSortMode.Value;

            pref.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
