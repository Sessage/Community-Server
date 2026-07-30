using Klassenbibliothek.Data;
using Klassenbibliothek.Hubs;
using Klassenbibliothek.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace TodoSuite.Server.Services;

public class TodoCustomFieldService : TodoWorkspaceServiceBase, ITodoCustomFieldService
{
    public TodoCustomFieldService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IHubContext<TodoHubEndpoint> hubContext,
        IWebHostEnvironment env,
        ITaskMemberService taskMemberService)
        : base(dbContextFactory, hubContext, env, taskMemberService)
    {
    }

    public async Task<TodoCustomFieldDefinitionEntity?> AddFieldAsync(string userId, Guid listId, TodoCustomFieldDefinitionEntity field, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.CustomFields).ThenInclude(field => field.Options)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return null;
        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Benutzerdefinierte Felder können nur von Admins geändert werden (Liste='{list.Name}', User='{userId}').");

        // Mobile clients create stable IDs before going offline. If the server committed the
        // first request but the response was lost, retrying must be idempotent.
        if (field.Id != Guid.Empty)
        {
            var existing = list.CustomFields.FirstOrDefault(x => x.Id == field.Id);
            if (existing is not null)
                return existing;
        }

        var name = (field.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Feldname darf nicht leer sein.", nameof(field));

        var nextOrder = list.CustomFields.Any() ? list.CustomFields.Max(x => x.SortOrder) + 1 : 0;
        var sourceTaskListId = await NormalizeSourceTaskListIdAsync(db, userId, list, field, cancellationToken);
        var entity = new TodoCustomFieldDefinitionEntity
        {
            Id = field.Id == Guid.Empty ? Guid.NewGuid() : field.Id,
            ListId = listId,
            Name = name,
            Type = field.Type,
            IsRequired = field.IsRequired,
            SourceTaskListId = sourceTaskListId,
            SortOrder = nextOrder,
            Options = NormalizeOptions(field.Options, field.Type)
        };

        db.TodoCustomFields.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
        return entity;
    }

    public async Task<TodoCustomFieldDefinitionEntity?> UpdateFieldAsync(string userId, Guid listId, TodoCustomFieldDefinitionEntity field, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return null;
        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Benutzerdefinierte Felder können nur von Admins geändert werden (Liste='{list.Name}', User='{userId}').");

        var entity = await db.TodoCustomFields
            .FirstOrDefaultAsync(x => x.Id == field.Id && x.ListId == listId, cancellationToken);

        if (entity is null) return null;

        var name = (field.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Feldname darf nicht leer sein.", nameof(field));

        entity.Name = name;
        entity.Type = field.Type;
        entity.IsRequired = field.IsRequired;
        entity.SourceTaskListId = await NormalizeSourceTaskListIdAsync(db, userId, list, field, cancellationToken);
        await ReplaceOptionsAsync(db, entity.Id, field.Options, entity.Type, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
        entity.Options = await db.TodoCustomFieldOptions
            .Where(option => option.FieldId == entity.Id)
            .OrderBy(option => option.SortOrder)
            .ToListAsync(cancellationToken);
        return entity;
    }

    public async Task<bool> DeleteFieldAsync(string userId, Guid listId, Guid fieldId, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return false;
        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Benutzerdefinierte Felder können nur von Admins geändert werden (Liste='{list.Name}', User='{userId}').");

        var entity = await db.TodoCustomFields.FirstOrDefaultAsync(x => x.Id == fieldId && x.ListId == listId, cancellationToken);
        if (entity is null) return false;

        db.TodoCustomFields.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
        return true;
    }

    public async Task ReorderFieldsAsync(string userId, Guid listId, IReadOnlyList<Guid> orderedFieldIds, CancellationToken cancellationToken = default)
    {
        await using var db = await DbContextFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.TodoLists
            .Include(l => l.Participants)
            .Include(l => l.CustomFields)
            .FirstOrDefaultAsync(l => l.Id == listId && l.DeletedAt == null, cancellationToken);

        if (list is null) return;
        if (!CanAdmin(userId, list))
            throw new UnauthorizedAccessException($"Benutzerdefinierte Felder können nur von Admins geändert werden (Liste='{list.Name}', User='{userId}').");

        var fields = list.CustomFields.ToDictionary(x => x.Id);
        var seen = new HashSet<Guid>();
        var i = 0;
        foreach (var id in orderedFieldIds ?? [])
        {
            if (!fields.TryGetValue(id, out var field)) continue;
            field.SortOrder = i++;
            seen.Add(id);
        }

        foreach (var field in list.CustomFields.Where(x => !seen.Contains(x.Id)).OrderBy(x => x.SortOrder))
            field.SortOrder = i++;

        await db.SaveChangesAsync(cancellationToken);
        await NotifyListUpdatedAsync(listId, cancellationToken);
    }

    private static List<TodoCustomFieldOptionEntity> NormalizeOptions(IEnumerable<TodoCustomFieldOptionEntity>? options, TodoCustomFieldType type)
    {
        if (type is not (TodoCustomFieldType.Dropdown or TodoCustomFieldType.MultiSelect))
            return new();

        return NormalizeOptionValues(options)
            .Select((value, index) => new TodoCustomFieldOptionEntity { Id = Guid.NewGuid(), Value = value, SortOrder = index })
            .ToList();
    }

    private static async Task ReplaceOptionsAsync(
        ApplicationDbContext db,
        Guid fieldId,
        IEnumerable<TodoCustomFieldOptionEntity>? incomingOptions,
        TodoCustomFieldType type,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            await db.TodoCustomFieldOptions
                .Where(option => option.FieldId == fieldId)
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            var existing = await db.TodoCustomFieldOptions
                .Where(option => option.FieldId == fieldId)
                .ToListAsync(cancellationToken);
            db.TodoCustomFieldOptions.RemoveRange(existing);
        }

        db.TodoCustomFieldOptions.AddRange(NormalizeOptions(incomingOptions, type, fieldId));
    }

    private static List<TodoCustomFieldOptionEntity> NormalizeOptions(
        IEnumerable<TodoCustomFieldOptionEntity>? options,
        TodoCustomFieldType type,
        Guid fieldId)
        => NormalizeOptions(options, type)
            .Select((option, index) =>
            {
                option.FieldId = fieldId;
                option.SortOrder = index;
                return option;
            })
            .ToList();

    private static List<string> NormalizeOptionValues(IEnumerable<TodoCustomFieldOptionEntity>? options)
        => (options ?? [])
            .Select(x => (x.Value ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static async Task<Guid?> NormalizeSourceTaskListIdAsync(
        ApplicationDbContext db,
        string userId,
        TodoListEntity currentList,
        TodoCustomFieldDefinitionEntity field,
        CancellationToken cancellationToken)
    {
        if (field.Type != TodoCustomFieldType.TaskTitleSelect)
            return null;

        var sourceListId = field.SourceTaskListId;
        if (sourceListId is null || sourceListId == Guid.Empty)
            throw new ArgumentException("Für Aufgaben-Auswahl muss eine Quellliste ausgewählt werden.", nameof(field));

        if (sourceListId == currentList.Id)
            throw new ArgumentException("Für Aufgaben-Auswahl muss eine andere Liste als Quellliste ausgewählt werden.", nameof(field));

        var sourceList = await db.TodoLists
            .Include(l => l.Participants)
            .FirstOrDefaultAsync(l => l.Id == sourceListId.Value && l.DeletedAt == null, cancellationToken);

        if (sourceList is null)
            throw new ArgumentException("Die ausgewählte Quellliste wurde nicht gefunden.", nameof(field));

        if (!CanAdmin(userId, sourceList))
            throw new UnauthorizedAccessException("Aufgaben-Auswahl kann nur mit Quelllisten erstellt werden, in denen der Benutzer Admin ist.");

        return sourceList.Id;
    }
}
