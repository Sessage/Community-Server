using System.Security.Claims;

namespace TodoSuite.Server.Auth;

/// <summary>
/// Prevents a request authenticated by more than one configured API scheme from combining
/// claims that belong to different accounts. This can otherwise make authorization checks
/// use one identity while application code resolves the subject from another.
/// </summary>
public static class ApiIdentityBinding
{
    public static bool HasSingleAuthenticatedSubject(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var authenticated = principal.Identities
            .Where(identity => identity.IsAuthenticated)
            .ToArray();
        if (authenticated.Length == 0)
            return false;

        string? subject = null;
        foreach (var identity in authenticated)
        {
            var current = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? identity.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(current))
                return false;
            if (subject is null)
                subject = current;
            else if (!string.Equals(subject, current, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
