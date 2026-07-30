using System.Text.RegularExpressions;

namespace TodoSuite.Server.Services;

/// <summary>
/// Löst AD-/LDAP-Standardwerte auf und erzeugt ausschließlich aus validierten
/// Attributnamen und LDAP-escaped Eingaben Suchfilter.
/// </summary>
public static partial class LdapDirectoryConfiguration
{
    public static bool IsActiveDirectory(ActiveDirectoryOptions options) =>
        !options.Provider.Equals("Ldap", StringComparison.OrdinalIgnoreCase) &&
        !options.Provider.Equals("GenericLdap", StringComparison.OrdinalIgnoreCase);

    public static string UserNameAttribute(ActiveDirectoryOptions options) =>
        AttributeOrDefault(options.UserNameAttribute, IsActiveDirectory(options) ? "sAMAccountName" : "uid");

    public static IReadOnlyList<string> UserNameAttributes(ActiveDirectoryOptions options)
    {
        var configuredAdditional = string.IsNullOrWhiteSpace(options.AdditionalUserNameAttributes)
            ? (IsActiveDirectory(options) ? ["userPrincipalName"] : Array.Empty<string>())
            : options.AdditionalUserNameAttributes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return configuredAdditional.Prepend(UserNameAttribute(options))
            .Select(ValidateAttributeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string EmailAttribute(ActiveDirectoryOptions options) =>
        AttributeOrDefault(options.EmailAttribute, "mail");

    public static string DisplayNameAttribute(ActiveDirectoryOptions options) =>
        AttributeOrDefault(options.DisplayNameAttribute, IsActiveDirectory(options) ? "displayName" : "cn");

    public static string? IdentityAttribute(ActiveDirectoryOptions options) =>
        string.IsNullOrWhiteSpace(options.IdentityAttribute)
            ? (IsActiveDirectory(options) ? "userPrincipalName" : null)
            : ValidateAttributeName(options.IdentityAttribute);

    public static string UserObjectClass(ActiveDirectoryOptions options) =>
        AttributeOrDefault(options.UserObjectClass, IsActiveDirectory(options) ? "user" : "inetOrgPerson");

    public static string GroupObjectClass(ActiveDirectoryOptions options) =>
        AttributeOrDefault(options.GroupObjectClass, IsActiveDirectory(options) ? "group" : "groupOfNames");

    public static string GroupNameAttribute(ActiveDirectoryOptions options) =>
        AttributeOrDefault(options.GroupNameAttribute, "cn");

    public static string GroupMembershipAttribute(ActiveDirectoryOptions options) =>
        AttributeOrDefault(options.GroupMembershipAttribute, "memberOf");

    public static string BuildUserSearchFilter(ActiveDirectoryOptions options, string username)
    {
        var escapedUsername = EscapeFilterValue(username.Trim());
        if (!string.IsNullOrWhiteSpace(options.UserSearchFilter))
        {
            if (!options.UserSearchFilter.Contains("{username}", StringComparison.Ordinal))
                throw new InvalidOperationException("ActiveDirectory:UserSearchFilter muss den Platzhalter {username} enthalten.");

            return options.UserSearchFilter.Replace("{username}", escapedUsername, StringComparison.Ordinal);
        }

        var loginAttributes = string.Concat(UserNameAttributes(options).Select(x => $"({x}={escapedUsername})"));
        var loginFilter = UserNameAttributes(options).Count == 1 ? loginAttributes : $"(|{loginAttributes})";
        return $"(&(objectClass={EscapeFilterValue(UserObjectClass(options))}){loginFilter})";
    }

    public static string BuildPrincipalSearchFilter(ActiveDirectoryOptions options, string query)
    {
        var text = EscapeFilterValue(query.Trim());
        var userAttributes = UserNameAttributes(options)
            .Append(EmailAttribute(options)).Append(DisplayNameAttribute(options))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var userTerms = string.Concat(userAttributes.Select(x => $"({x}=*{text}*)"));
        var groupTerms = string.Concat(new[] { GroupNameAttribute(options), DisplayNameAttribute(options) }
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(x => $"({x}=*{text}*)"));
        return $"(|(&(objectClass={EscapeFilterValue(UserObjectClass(options))})(|{userTerms}))" +
               $"(&(objectClass={EscapeFilterValue(GroupObjectClass(options))})(|{groupTerms})))";
    }

    public static string? BuildGroupMembershipSearchFilter(
        ActiveDirectoryOptions options, string userDn, string username)
    {
        if (!string.IsNullOrWhiteSpace(options.GroupMembershipSearchFilter))
        {
            var filter = options.GroupMembershipSearchFilter
                .Replace("{userDn}", EscapeFilterValue(userDn), StringComparison.Ordinal)
                .Replace("{username}", EscapeFilterValue(username), StringComparison.Ordinal);
            if (filter == options.GroupMembershipSearchFilter)
                throw new InvalidOperationException(
                    "ActiveDirectory:GroupMembershipSearchFilter muss {userDn} oder {username} enthalten.");
            return filter;
        }

        return IsActiveDirectory(options)
            ? $"(&(objectClass=group)(member:1.2.840.113556.1.4.1941:={EscapeFilterValue(userDn)}))"
            : null;
    }

    public static IReadOnlyList<string> UserAttributes(ActiveDirectoryOptions options) =>
        UserNameAttributes(options)
            .Append(EmailAttribute(options))
            .Append(DisplayNameAttribute(options))
            .Append(GroupMembershipAttribute(options))
            .Concat(IdentityAttribute(options) is { } identity ? [identity] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> PrincipalAttributes(ActiveDirectoryOptions options) =>
        UserAttributes(options)
            .Append(GroupNameAttribute(options))
            .Append("description")
            .Append("objectClass")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string EscapeFilterValue(string input) => input
        .Replace("\\", "\\5c", StringComparison.Ordinal)
        .Replace("*", "\\2a", StringComparison.Ordinal)
        .Replace("(", "\\28", StringComparison.Ordinal)
        .Replace(")", "\\29", StringComparison.Ordinal)
        .Replace("\0", "\\00", StringComparison.Ordinal);

    public static string GetDomainFromBaseDn(string baseDn) => string.Join(".",
        baseDn.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.StartsWith("dc=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..]));

    private static string AttributeOrDefault(string configured, string fallback) =>
        ValidateAttributeName(string.IsNullOrWhiteSpace(configured) ? fallback : configured);

    private static string ValidateAttributeName(string value)
    {
        var trimmed = value.Trim();
        if (!AttributeNameRegex().IsMatch(trimmed))
            throw new InvalidOperationException($"Ungültiger LDAP-Attributname '{trimmed}'.");
        return trimmed;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeNameRegex();
}
