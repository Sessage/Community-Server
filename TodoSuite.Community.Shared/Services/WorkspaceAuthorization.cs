using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

/// <summary>
/// Canonical effective-admin checks shared by UI and server services. Identity keys may
/// contain the stable local user id and legacy e-mail/user-name aliases.
/// </summary>
public static class WorkspaceAuthorization
{
    public static bool CanAdminList(TodoListEntity? list, IEnumerable<string?> identityKeys)
    {
        if (list is null)
            return false;

        var keys = NormalizeKeys(identityKeys);
        return keys.Count > 0
               && (Matches(keys, list.OwnerId)
                   || list.Participants?.Any(participant =>
                       !participant.InvitationPending
                       && participant.Role == ListRole.Admin
                       && (Matches(keys, participant.UserId) || Matches(keys, participant.Email))) == true);
    }

    public static bool CanAdminPortfolio(
        TodoListGroupEntity? portfolio,
        IEnumerable<PortfolioParticipantEntity> participants,
        IEnumerable<string?> identityKeys)
    {
        if (portfolio is null || !portfolio.IsPortfolio)
            return false;

        var keys = NormalizeKeys(identityKeys);
        return keys.Count > 0
               && (Matches(keys, portfolio.OwnerId)
                   || participants.Any(participant =>
                       participant.PortfolioGroupId == portfolio.Id
                       && !participant.InvitationPending
                       && participant.Role == ListRole.Admin
                       && (Matches(keys, participant.UserId) || Matches(keys, participant.Email))));
    }

    public static bool Matches(IEnumerable<string?> identityKeys, string? candidate)
        => Matches(NormalizeKeys(identityKeys), candidate);

    private static HashSet<string> NormalizeKeys(IEnumerable<string?> identityKeys)
        => identityKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool Matches(HashSet<string> identityKeys, string? candidate)
        => !string.IsNullOrWhiteSpace(candidate) && identityKeys.Contains(candidate.Trim());
}
