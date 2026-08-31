using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

public static class AssigneeIdentityKeys
{
    public static HashSet<string> Create(string? userId, params string?[] aliases)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(keys, userId);
        foreach (var alias in aliases)
            Add(keys, alias);
        return keys;
    }

    public static HashSet<string> ExpandWithAcceptedParticipants(
        IEnumerable<string> initialKeys,
        IEnumerable<TodoListEntity> lists)
    {
        var keys = new HashSet<string>(initialKeys
            .Select(key => key?.Trim())
            .Where(key => !string.IsNullOrWhiteSpace(key))!, StringComparer.OrdinalIgnoreCase);

        var identities = lists
            .SelectMany(list => list.Participants ?? [])
            .Where(participant => !participant.InvitationPending)
            .Select(participant => new[] { participant.UserId, participant.Email }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .Where(identity => identity.Length > 0)
            .ToList();

        // Repeated expansion also handles legacy data where one list only contains an e-mail
        // address while another already contains the canonical user ID.
        bool changed;
        do
        {
            changed = false;
            foreach (var identity in identities.Where(identity => identity.Any(keys.Contains)))
                foreach (var value in identity)
                    changed |= keys.Add(value);
        } while (changed);

        return keys;
    }

    public static bool Matches(string? assignee, IReadOnlySet<string> keys)
        => !string.IsNullOrWhiteSpace(assignee) && keys.Contains(assignee.Trim());

    public static bool CanWrite(TodoListEntity list, IEnumerable<string> identityKeys)
    {
        var keys = ExpandWithAcceptedParticipants(identityKeys, [list]);
        if (Matches(list.OwnerId, keys)) return true;

        return (list.Participants ?? []).Any(participant =>
            !participant.InvitationPending
            && participant.Role != ListRole.Observer
            && (Matches(participant.UserId, keys) || Matches(participant.Email, keys)));
    }

    private static void Add(ISet<string> keys, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            keys.Add(value.Trim());
    }
}
