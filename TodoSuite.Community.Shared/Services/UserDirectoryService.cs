using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

namespace Klassenbibliothek.Services;

/// <summary>Minimal user-directory entry suitable for assignee and participant selectors.</summary>
public class ManagedUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }
}

/// <summary>
/// Maintains the in-memory directory used by UI controls and returns defensive snapshots to callers.
/// </summary>
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
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedTerm = term?.Trim();
        var values = _users.Values
            .Where(u => string.IsNullOrWhiteSpace(normalizedTerm)
                || u.DisplayName.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase)
                || u.Email.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Email, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Id)
            .Select(Clone)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyCollection<ManagedUser>>(values);
    }

    public Task<ManagedUser> AddOrUpdateAsync(ManagedUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        var displayName = (user.DisplayName ?? string.Empty).Trim();
        if (displayName.Length == 0)
            throw new ArgumentException("Der Anzeigename darf nicht leer sein.", nameof(user));

        var email = (user.Email ?? string.Empty).Trim();
        if (email.Length > 0 && !new EmailAddressAttribute().IsValid(email))
            throw new ArgumentException("Die E-Mail-Adresse ist ungültig.", nameof(user));

        var stored = new ManagedUser
        {
            Id = user.Id == Guid.Empty ? Guid.NewGuid() : user.Id,
            DisplayName = displayName,
            Email = email,
            IsAdmin = user.IsAdmin
        };
        _users[stored.Id] = stored;

        return Task.FromResult(Clone(stored));
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_users.TryRemove(id, out _));
    }

    private static ManagedUser Clone(ManagedUser user) => new()
    {
        Id = user.Id,
        DisplayName = user.DisplayName,
        Email = user.Email,
        IsAdmin = user.IsAdmin
    };
}
