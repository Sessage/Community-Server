using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;

namespace TodoSuite.Server.Services;

/// <summary>
/// Implementiert die Verwaltung von Labels.
/// </summary>
public class TodoLabelService : TodoWorkspaceServiceBase, ITodoLabelService
{
    private const int MaxLabelTitleLength = 200;

    /// <summary>
    /// Erstellt eine neue Instanz der Label-Verwaltung.
    /// </summary>
    public TodoLabelService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
    }

    /// <inheritdoc />
    public async Task<TodoLabelEntity?> AddLabelAsync(
        string userId,
        Guid listId,
        string title,
        string? backgroundColor,
        CancellationToken ct = default,
        Guid? id = null)
    {
        var name = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                $"Label konnte nicht angelegt werden: Titel ist leer. ListId='{listId}'.",
                nameof(title));
        if (name.Length > MaxLabelTitleLength)
            throw new ArgumentException($"Ein Labeltitel darf höchstens {MaxLabelTitleLength} Zeichen lang sein.", nameof(title));
        var color = NormalizeColor(backgroundColor);

        await using var db = await DbContextFactory.CreateDbContextAsync(ct);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, ct);

        if (list is null)
            throw new InvalidOperationException(
                $"Label konnte nicht angelegt werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException(
                $"Label kann nicht angelegt werden: Keine Berechtigung. Liste='{list.Name}', User='{userId}'.");

        if (id is { } requestedId && requestedId != Guid.Empty)
        {
            var existingById = await db.TodoLabels.AsNoTracking()
                .FirstOrDefaultAsync(label => label.Id == requestedId && label.ListId == listId, ct);
            if (existingById is not null) return existingById;
        }

        var exists = await db.TodoLabels.AnyAsync(
            l => l.ListId == listId && l.Title.ToLower() == name.ToLower(),
            ct);

        if (exists)
            throw new InvalidOperationException(
                $"Label '{name}' existiert bereits. Liste='{list.Name}'.");

        var entity = new TodoLabelEntity
        {
            Id = id is { } requestedIdValue && requestedIdValue != Guid.Empty ? requestedIdValue : Guid.NewGuid(),
            ListId = listId,
            Title = name,
            BackgroundColor = color
        };

        db.TodoLabels.Add(entity);
        await db.SaveChangesAsync(ct);

        await NotifyListUpdatedAsync(listId, ct);

        return entity;
    }

    /// <inheritdoc />
    public async Task<TodoLabelEntity?> UpdateLabelAsync(
        string userId,
        Guid listId,
        Guid labelId,
        string title,
        string? backgroundColor,
        CancellationToken ct = default)
    {
        var name = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                $"Label konnte nicht geändert werden: Titel ist leer. LabelId='{labelId}'.",
                nameof(title));
        if (name.Length > MaxLabelTitleLength)
            throw new ArgumentException($"Ein Labeltitel darf höchstens {MaxLabelTitleLength} Zeichen lang sein.", nameof(title));
        var color = NormalizeColor(backgroundColor);

        await using var db = await DbContextFactory.CreateDbContextAsync(ct);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, ct);

        if (list is null)
            throw new InvalidOperationException(
                $"Label konnte nicht geändert werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException(
                $"Label kann nicht geändert werden: Keine Berechtigung. Liste='{list.Name}', User='{userId}'.");

        var label = await db.TodoLabels
            .FirstOrDefaultAsync(l => l.Id == labelId && l.ListId == listId, ct);

        if (label is null)
            return null;

        var nameConflict = await db.TodoLabels.AnyAsync(
            l => l.ListId == listId
                 && l.Id != labelId
                 && l.Title.ToLower() == name.ToLower(),
            ct);

        if (nameConflict)
            throw new InvalidOperationException(
                $"Label konnte nicht geändert werden: Ein Label mit dem Namen '{name}' existiert bereits. Liste='{list.Name}'.");

        label.Title = name;
        label.BackgroundColor = color;

        await db.SaveChangesAsync(ct);
        await NotifyListUpdatedAsync(listId, ct);

        return label;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteLabelAsync(
        string userId,
        Guid listId,
        Guid labelId,
        CancellationToken ct = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(ct);

        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, ct);

        if (list is null)
            throw new InvalidOperationException(
                $"Label konnte nicht gelöscht werden: Liste nicht gefunden. ListId='{listId}'.");

        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException(
                $"Label kann nicht gelöscht werden: Keine Berechtigung. Liste='{list.Name}', User='{userId}'.");

        var label = await db.TodoLabels
            .FirstOrDefaultAsync(l => l.Id == labelId && l.ListId == listId, ct);

        if (label is null)
            return false;

        db.TodoLabels.Remove(label);

        await db.SaveChangesAsync(ct);
        await NotifyListUpdatedAsync(listId, ct);

        return true;
    }

    private static string? NormalizeColor(string? backgroundColor)
    {
        var color = (backgroundColor ?? string.Empty).Trim();
        if (color.Length == 0)
            return null;
        if (color.Length != 7 || color[0] != '#' || color.Skip(1).Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Die Labelfarbe muss als sechsstelliger Hex-Farbwert angegeben werden.", nameof(backgroundColor));
        return color.ToUpperInvariant();
    }
}
