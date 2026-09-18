using System.DirectoryServices.Protocols;
using System.Text;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Services;

/// <summary>Ergebnis einer erfolgreichen AD-/LDAP-Authentifizierung.</summary>
public record LdapUserInfo(
    string UserName,
    string Email,
    string DisplayName,
    DirectoryIdentitySnapshot DirectoryIdentity);

/// <summary>
/// Authenticates a user against the configured LDAP/Active Directory server and maps the result
/// to the minimal identity data needed by the local account bridge.
/// </summary>
/// <remarks>
/// Search filters are escaped and certificate validation follows the configured trust policy.
/// Callers should surface only generic login failures because directory errors may contain infrastructure details.
/// </remarks>
public class LdapAuthService
{
    private readonly ActiveDirectoryOptions _options;
    private readonly ILogger<LdapAuthService> _logger;

    public LdapAuthService(ActiveDirectoryOptions options, ILogger<LdapAuthService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<LdapUserInfo?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return Task.FromResult<LdapUserInfo?>(null);

        return Task.Run(() => Authenticate(username, password), ct);
    }

    private LdapUserInfo? Authenticate(string username, string password)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        try
        {
            // Search with the configured service/anonymous identity first. The submitted
            // password is verified only by a separate bind as the exact discovered user DN.
            using var connection = LdapDirectoryConnection.CreateAndBindSearchConnection(_options, _logger);
            var searchRequest = new SearchRequest(
                _options.BaseDn,
                LdapDirectoryConfiguration.BuildUserSearchFilter(_options, username),
                SearchScope.Subtree,
                LdapDirectoryConfiguration.UserAttributes(_options).ToArray());
            var searchResponse = (SearchResponse)connection.SendRequest(searchRequest);

            if (searchResponse.Entries.Count == 0)
            {
                _logger.LogWarning("LDAP: Benutzer '{Username}' nicht gefunden.", username);
                return null;
            }

            // Ein nicht eindeutiger Filter darf niemals zufällig einen Benutzer authentifizieren.
            if (searchResponse.Entries.Count != 1)
            {
                _logger.LogWarning("LDAP: Suche nach '{Username}' lieferte {Count} Einträge.",
                    username, searchResponse.Entries.Count);
                return null;
            }

            var entry = searchResponse.Entries[0];
            var userDn = entry.DistinguishedName;

            try
            {
                // Do not try domain/name variants for the user bind: the directory-provided DN
                // is the unambiguous identity whose credentials must be proven.
                using var verifyConnection = LdapDirectoryConnection.CreateAndBindConnection(
                    _options, _logger, userDn, password, allowAdCredentialVariants: false);
            }
            catch (LdapException ex)
            {
                _logger.LogWarning(ex, "LDAP: Passwortprüfung für Benutzer '{Username}' fehlgeschlagen.", username);
                return null;
            }

            var groupIds = FindGroupDns(connection, entry, userDn, username);
            if (!IsInRequiredGroup(groupIds))
            {
                _logger.LogWarning("LDAP: Benutzer '{Username}' ist nicht Mitglied der erforderlichen Gruppe.", username);
                return null;
            }

            var userNameAttribute = LdapDirectoryConfiguration.UserNameAttribute(_options);
            var directoryUserName = GetAttributeValue(entry, userNameAttribute) ?? username;
            var email = GetAttributeValue(entry, LdapDirectoryConfiguration.EmailAttribute(_options));
            email = ResolveEmail(email, username, directoryUserName);
            if (string.IsNullOrWhiteSpace(email))
            {
                _logger.LogWarning(
                    "LDAP: Benutzer '{Username}' besitzt keine nutzbare E-Mail-Adresse. " +
                    "Konfigurieren Sie EmailAttribute oder FallbackEmailDomain.", username);
                return null;
            }

            var displayName = GetAttributeValue(entry, LdapDirectoryConfiguration.DisplayNameAttribute(_options))
                              ?? directoryUserName;
            var identityAttribute = LdapDirectoryConfiguration.IdentityAttribute(_options);
            var directoryIdentity = identityAttribute is null ? null : GetAttributeValue(entry, identityAttribute);

            return new LdapUserInfo(directoryUserName, email, displayName,
                new DirectoryIdentitySnapshot(userDn, directoryIdentity ?? email, displayName, groupIds));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP: Unerwarteter Fehler bei der Authentifizierung für Benutzer '{Username}'.", username);
            return null;
        }
    }

    private IReadOnlyCollection<string> FindGroupDns(
        LdapConnection connection, SearchResultEntry userEntry, string userDn, string username)
    {
        var membershipFilter = LdapDirectoryConfiguration.BuildGroupMembershipSearchFilter(
            _options, userDn, username);
        if (membershipFilter is not null)
        {
            try
            {
                var searchBase = LdapDirectoryConfiguration.GroupSearchBase(_options);
                var request = new SearchRequest(searchBase, membershipFilter, SearchScope.Subtree, "distinguishedName");
                var response = (SearchResponse)connection.SendRequest(request);
                return response.Entries.Cast<SearchResultEntry>()
                    .Select(x => x.DistinguishedName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (LdapDirectoryConfiguration.IsActiveDirectory(_options) &&
                                       string.IsNullOrWhiteSpace(_options.GroupMembershipSearchFilter))
            {
                // Nicht jeder LDAP-kompatible Server unterstützt die rekursive AD Matching Rule.
                _logger.LogDebug(ex, "LDAP: Verschachtelte Gruppen konnten nicht aufgelöst werden; verwende direkte Mitgliedschaften.");
            }
        }

        var membershipAttribute = LdapDirectoryConfiguration.GroupMembershipAttribute(_options);
        var memberOf = userEntry.Attributes[membershipAttribute];
        return memberOf is null
            ? []
            : memberOf.GetValues(typeof(string)).Cast<string>()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private bool IsInRequiredGroup(IReadOnlyCollection<string> groupDns)
    {
        if (!string.IsNullOrWhiteSpace(_options.RequiredGroupDn))
            return groupDns.Contains(_options.RequiredGroupDn.Trim(), StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(_options.RequiredGroupCn))
            return true;

        var expectedRdn = $"{LdapDirectoryConfiguration.GroupNameAttribute(_options)}={_options.RequiredGroupCn.Trim()}";
        return groupDns.Any(dn => dn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals(expectedRdn, StringComparison.OrdinalIgnoreCase)));
    }

    private string? ResolveEmail(string? configuredEmail, string login, string directoryUserName)
    {
        if (!string.IsNullOrWhiteSpace(configuredEmail))
            return configuredEmail.Trim();
        if (login.Contains('@'))
            return login;

        var fallbackDomain = _options.FallbackEmailDomain.Trim();
        if (string.IsNullOrWhiteSpace(fallbackDomain) && LdapDirectoryConfiguration.IsActiveDirectory(_options))
            fallbackDomain = LdapDirectoryConfiguration.GetDomainFromBaseDn(_options.BaseDn);

        return string.IsNullOrWhiteSpace(fallbackDomain) ? null : $"{directoryUserName}@{fallbackDomain}";
    }

    private static string? GetAttributeValue(SearchResultEntry entry, string attributeName)
    {
        var attribute = entry.Attributes[attributeName];
        if (attribute is null || attribute.Count == 0)
            return null;
        var value = attribute[0];
        return (value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value?.ToString())?.Trim();
    }
}
