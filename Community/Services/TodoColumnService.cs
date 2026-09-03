using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;

namespace TodoSuite.Server.Services;

/// <summary>
/// Implementiert die Spaltenverwaltung für Listen.
/// </summary>
public class TodoColumnService : TodoWorkspaceServiceBase, ITodoColumnService
{
    /// <summary>
    /// Erstellt eine neue Instanz der Spaltenverwaltung.
    /// </summary>
    public TodoColumnService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
    }

    /// <inheritdoc />
    public async Task AddColumnAsync(string userId, Guid listId, string columnName, CancellationToken cancellationToken = default)
    {
        var name = (columnName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"Spalte konnte nicht angelegt werden: Name ist leer. ListId='{listId}'.", nameof(columnName));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            throw new InvalidOperationException($"Spalte konnte nicht angelegt werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Spalte kann nicht angelegt werden: Keine Berechtigung. Liste='{list.Name}', User='{userId}'.");

        list.Columns ??= new List<string>();

        if (list.Columns.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;

        list.Columns.Add(name);

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RenameColumnAsync(string userId, Guid listId, string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var oldTrim = (oldName ?? "").Trim();
        var newTrim = (newName ?? "").Trim();

        if (string.IsNullOrWhiteSpace(oldTrim))
            throw new ArgumentException($"Spalte konnte nicht umbenannt werden: Alter Name ist leer. ListId='{listId}'.", nameof(oldName));

        if (string.IsNullOrWhiteSpace(newTrim))
            throw new ArgumentException($"Spalte konnte nicht umbenannt werden: Neuer Name ist leer. ListId='{listId}'.", nameof(newName));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.Tasks)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            throw new InvalidOperationException($"Spalte konnte nicht umbenannt werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Spalte kann nicht umbenannt werden: Keine Berechtigung. Liste='{list.Name}', User='{userId}'.");

        list.Columns ??= new List<string>();

        var idx = list.Columns.FindIndex(c => c.Equals(oldTrim, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new InvalidOperationException($"Spalte konnte nicht umbenannt werden: '{oldTrim}' existiert nicht. Liste='{list.Name}'.");

        if (list.Columns.Any(c => c.Equals(newTrim, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Spalte konnte nicht umbenannt werden: Zielname '{newTrim}' existiert bereits. Liste='{list.Name}'.");

        list.Columns[idx] = newTrim;

        foreach (var t in list.Tasks.Where(t => t.Column == oldTrim))
            t.Column = newTrim;

        // DoneColumns-Eintrag mitumbenennen
        list.DoneColumns ??= new List<string>();
        var doneIdx = list.DoneColumns.FindIndex(c => c.Equals(oldTrim, StringComparison.OrdinalIgnoreCase));
        if (doneIdx >= 0)
            list.DoneColumns[doneIdx] = newTrim;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteColumnAsync(
        string userId,
        Guid listId,
        string columnName,
        bool deleteTasksInColumn,
        string? moveTasksToColumn,
        CancellationToken cancellationToken = default)
    {
        var col = (columnName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(col))
            throw new ArgumentException($"Spalte konnte nicht gelöscht werden: Name ist leer. ListId='{listId}'.", nameof(columnName));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.Tasks)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            throw new InvalidOperationException($"Spalte konnte nicht gelöscht werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Spalte kann nicht gelöscht werden: Keine Berechtigung. Liste='{list.Name}', User='{userId}'.");

        list.Columns ??= new List<string>();

        var idx = list.Columns.FindIndex(c => c.Equals(col, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return;

        string? target = null;
        var moveTo = (moveTasksToColumn ?? "").Trim();

        if (!deleteTasksInColumn)
        {
            if (!string.IsNullOrWhiteSpace(moveTo) &&
                list.Columns.Any(c => c.Equals(moveTo, StringComparison.OrdinalIgnoreCase)) &&
                !moveTo.Equals(col, StringComparison.OrdinalIgnoreCase))
            {
                target = list.Columns.First(c => c.Equals(moveTo, StringComparison.OrdinalIgnoreCase));
            }

            var remaining = list.Columns.Where(c => !c.Equals(col, StringComparison.OrdinalIgnoreCase)).ToList();
            target ??= remaining.FirstOrDefault();
        }

        var affected = list.Tasks.Where(t => t.Column == col).ToList();

        if (deleteTasksInColumn)
        {
            db.TodoTasks.RemoveRange(affected);
        }
        else
        {
            foreach (var t in affected)
            {
                t.Column = target ?? "";
                t.KanbanSortOrder = 0;
            }
        }

        list.Columns.RemoveAt(idx);

        // DoneColumns-Eintrag ebenfalls entfernen
        list.DoneColumns ??= new List<string>();
        list.DoneColumns.RemoveAll(c => c.Equals(col, StringComparison.OrdinalIgnoreCase));

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReorderColumnsAsync(
        string userId,
        Guid listId,
        IReadOnlyList<string> orderedColumns,
        CancellationToken cancellationToken = default)
    {
        if (orderedColumns is null)
            throw new ArgumentNullException(nameof(orderedColumns));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            throw new InvalidOperationException($"Spaltenreihenfolge konnte nicht gespeichert werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Spaltenreihenfolge kann nicht geändert werden: Keine Berechtigung. Liste='{list.Name}', User='{userId}'.");

        list.Columns ??= new List<string>();

        var currentSet = new HashSet<string>(list.Columns, StringComparer.OrdinalIgnoreCase);
        var incoming = orderedColumns
            .Select(c => (c ?? "").Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        var incomingSet = new HashSet<string>(incoming, StringComparer.OrdinalIgnoreCase);

        if (!incomingSet.SetEquals(currentSet))
            throw new InvalidOperationException(
                $"Spaltenreihenfolge konnte nicht gespeichert werden: Spaltenmenge stimmt nicht überein. " +
                $"Liste='{list.Name}'. Aktuell={string.Join(", ", list.Columns)}. Neu={string.Join(", ", incoming)}.");

        list.Columns = incoming;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetDoneColumnAsync(string userId, Guid listId, string columnName, bool isDone, CancellationToken cancellationToken = default)
    {
        var col = (columnName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(col))
            throw new ArgumentException($"Erledigt-Spalte konnte nicht gesetzt werden: Name ist leer. ListId='{listId}'.", nameof(columnName));

        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null)
            throw new InvalidOperationException($"Erledigt-Spalte konnte nicht gesetzt werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Erledigt-Spalte kann nicht gesetzt werden: Keine Berechtigung. Liste='{list.Name}', User='{userId}'.");

        if (!list.Columns.Any(c => c.Equals(col, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Erledigt-Spalte konnte nicht gesetzt werden: Spalte '{col}' existiert nicht. Liste='{list.Name}'.");

        list.DoneColumns ??= new List<string>();

        var alreadyDone = list.DoneColumns.Any(c => c.Equals(col, StringComparison.OrdinalIgnoreCase));

        if (isDone && !alreadyDone)
            list.DoneColumns.Add(col);
        else if (!isDone && alreadyDone)
            list.DoneColumns.RemoveAll(c => c.Equals(col, StringComparison.OrdinalIgnoreCase));
        else
            return; // Keine Änderung nötig

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }
}
