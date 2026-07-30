using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

namespace Klassenbibliothek.Services;

public class ManagedUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }
}

public class UserDirectoryService
{
    private readonly ConcurrentDictionary<Guid, ManagedUser> _users = new();

    public UserDirectoryService()
    {
        var initial = new ManagedUser
        {
            DisplayName = "System Administrator",
            Email = "admin@sessage.local",
            IsAdmin = true
        };
        _users.TryAdd(initial.Id, initial);
    }

    public Task<IReadOnlyCollection<ManagedUser>> SearchAsync(string? term, CancellationToken cancellationToken = default)
    {
        var values = _users.Values
            .Where(u => string.IsNullOrWhiteSpace(term)
                || u.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || u.Email.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.DisplayName)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyCollection<ManagedUser>>(values);
    }

    public Task<ManagedUser> AddOrUpdateAsync(ManagedUser user, CancellationToken cancellationToken = default)
    {
        if (user.Id == Guid.Empty)
        {
            user.Id = Guid.NewGuid();
        }

        _users[user.Id] = new ManagedUser
        {
            Id = user.Id,
            DisplayName = user.DisplayName,
            Email = user.Email,
            IsAdmin = user.IsAdmin
        };

        return Task.FromResult(user);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_users.TryRemove(id, out _));
    }
}
