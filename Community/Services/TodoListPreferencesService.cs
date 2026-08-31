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
        if (view.HasValue && !Enum.IsDefined(view.Value))
            throw new ArgumentOutOfRangeException(nameof(view));
        if (listSortMode.HasValue && !Enum.IsDefined(listSortMode.Value))
            throw new ArgumentOutOfRangeException(nameof(listSortMode));
        if (kanbanSortMode.HasValue && !Enum.IsDefined(kanbanSortMode.Value))
            throw new ArgumentOutOfRangeException(nameof(kanbanSortMode));

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

        var created = pref is null;
        if (created)
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
            ApplyPreference(pref!, view, listSortMode, kanbanSortMode);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (created)
        {
            // Two devices can create the first preference for the same list at the same time.
            // Retry against the row that won the unique (UserId,ListId) race.
            db.ChangeTracker.Clear();
            pref = await db.ListViewPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId && p.ListId == listId, cancellationToken);
            if (pref is null)
                throw;

            ApplyPreference(pref, view, listSortMode, kanbanSortMode);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static void ApplyPreference(
        ListViewPreferenceEntity preference,
        DefaultListView? view,
        ListSortMode? listSortMode,
        ListSortMode? kanbanSortMode)
    {
        if (view.HasValue) preference.LastView = view.Value;
        if (listSortMode.HasValue) preference.ListSortMode = listSortMode.Value;
        if (kanbanSortMode.HasValue) preference.KanbanSortMode = kanbanSortMode.Value;
        preference.UpdatedAtUtc = DateTime.UtcNow;
    }
}
