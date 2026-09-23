using Klassenbibliothek.Data;

namespace TodoSuite.Server.Services;

/// <summary>Applies attributes verified by LDAP to the matching local account.</summary>
public static class DirectoryLoginAccountUpdates
{
    public static bool Apply(
        ApplicationUser user,
        string email,
        string displayName,
        string normalizedEmail,
        string normalizedUserName)
    {
        var changed = !string.Equals(user.DisplayName, displayName, StringComparison.Ordinal)
            || !string.Equals(user.Email, email, StringComparison.Ordinal)
            || !string.Equals(user.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)
            || !user.EmailConfirmed;

        user.DisplayName = displayName;
        user.Email = email;
        user.NormalizedEmail = normalizedEmail;
        user.EmailConfirmed = true;

        if (user.UserName?.EndsWith("@local.invalid", StringComparison.OrdinalIgnoreCase) == true)
        {
            changed |= !string.Equals(user.UserName, email, StringComparison.Ordinal)
                || !string.Equals(user.NormalizedUserName, normalizedUserName, StringComparison.Ordinal);
            user.UserName = email;
            user.NormalizedUserName = normalizedUserName;
        }

        return changed;
    }
}
