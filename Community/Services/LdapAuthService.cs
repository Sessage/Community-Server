using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using System.Security.Cryptography;
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
            using var connection = CreateAndBindSearchConnection();
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
                using var verifyConnection = CreateAndBindConnection(userDn, password, allowAdCredentialVariants: false);
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
                var searchBase = string.IsNullOrWhiteSpace(_options.GroupSearchBaseDn)
                    ? _options.BaseDn
                    : _options.GroupSearchBaseDn;
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

    private LdapConnection CreateAndBindSearchConnection()
    {
        if (string.IsNullOrWhiteSpace(_options.BindUser))
            return CreateAndBindConnection(null, null, allowAdCredentialVariants: false);
        return CreateAndBindConnection(_options.BindUser, _options.BindPassword, allowAdCredentialVariants: true);
    }

    private LdapConnection CreateAndBindConnection(string? username, string? password, bool allowAdCredentialVariants)
    {
        var errors = new List<string>();
        var credentialVariants = BuildCredentialVariants(username, allowAdCredentialVariants).ToArray();

        foreach (var credentialVariant in credentialVariants)
        {
            foreach (var strategy in BuildConnectionStrategies())
            {
                LdapConnection? connection = null;
                try
                {
                    connection = CreateConnection(strategy.Port, strategy.UseSsl, strategy.UseStartTls);
                    if (credentialVariant is null)
                        connection.Bind();
                    else
                        connection.Bind(new NetworkCredential(credentialVariant, password));

                    _logger.LogInformation(
                        "LDAP: Bind erfolgreich via {Mode} gegen {Server}:{Port}.",
                        strategy.Name, _options.Server, strategy.Port);
                    return connection;
                }
                catch (Exception ex)
                {
                    connection?.Dispose();
                    var serverDetails = ex is LdapException ldap && !string.IsNullOrWhiteSpace(ldap.ServerErrorMessage)
                        ? $" ({ldap.ServerErrorMessage})"
                        : string.Empty;
                    errors.Add($"{strategy.Name}: {ex.Message}{serverDetails}");
                }
            }
        }

        throw new LdapException($"LDAP: Alle Bind-Versuche fehlgeschlagen. Details: {string.Join(" | ", errors)}");
    }

    private IEnumerable<string?> BuildCredentialVariants(string? username, bool allowAdCredentialVariants)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            yield return null;
            yield break;
        }

        yield return username;
        if (!allowAdCredentialVariants || !LdapDirectoryConfiguration.IsActiveDirectory(_options) ||
            username.Contains('=') || username.Contains(','))
            yield break;

        var domain = LdapDirectoryConfiguration.GetDomainFromBaseDn(_options.BaseDn);
        if (!username.Contains('@') && !string.IsNullOrWhiteSpace(domain))
            yield return $"{username}@{domain}";
        if (!username.Contains('\\') && !string.IsNullOrWhiteSpace(domain))
            yield return $"{domain.Split('.')[0].ToUpperInvariant()}\\{username}";
    }

    private IEnumerable<(string Name, int Port, bool UseSsl, bool UseStartTls)> BuildConnectionStrategies()
    {
        yield return ("konfiguriert", _options.Port, _options.UseSSL, _options.UseStartTls);
        if (!_options.EnableAutoFallback)
            yield break;

        var configured = (_options.Port, _options.UseSSL, _options.UseStartTls);
        foreach (var fallback in new[]
                 {
                     (Port: 389, UseSsl: false, UseStartTls: false),
                     (Port: 389, UseSsl: false, UseStartTls: true),
                     (Port: 636, UseSsl: true, UseStartTls: false)
                 }.Where(x => x != configured))
            yield return ("Fallback", fallback.Port, fallback.UseSsl, fallback.UseStartTls);
    }

    private LdapConnection CreateConnection(int port, bool useSsl, bool useStartTls)
    {
        if (useSsl && useStartTls)
            throw new InvalidOperationException("UseSSL und UseStartTls dürfen nicht gleichzeitig aktiviert sein.");
        if (string.IsNullOrWhiteSpace(_options.Server) || string.IsNullOrWhiteSpace(_options.BaseDn))
            throw new InvalidOperationException("Für LDAP müssen Server und BaseDn konfiguriert sein.");

        var connection = new LdapConnection(new LdapDirectoryIdentifier(_options.Server, port))
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 300))
        };
        connection.SessionOptions.ProtocolVersion = 3;
        ConfigureCertificatePin(connection, useSsl || useStartTls);
        if (useSsl)
            connection.SessionOptions.SecureSocketLayer = true;
        if (useStartTls)
            connection.SessionOptions.StartTransportLayerSecurity(null);
        return connection;
    }

    private void ConfigureCertificatePin(LdapConnection connection, bool encryptedTransport)
    {
        var configured = _options.PinnedServerCertificateSha256;
        if (string.IsNullOrWhiteSpace(configured)) return;
        if (!encryptedTransport)
            throw new InvalidOperationException("Ein LDAP-Zertifikat-Pin erfordert UseSSL oder UseStartTls.");

        var normalized = configured
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(normalized);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("PinnedServerCertificateSha256 ist kein gültiger hexadezimaler SHA-256-Fingerabdruck.", ex);
        }
        if (expected.Length != SHA256.HashSizeInBytes)
            throw new InvalidOperationException("PinnedServerCertificateSha256 muss genau 32 Bytes enthalten.");

        connection.SessionOptions.VerifyServerCertificate = (_, certificate) =>
        {
            var actual = SHA256.HashData(certificate.GetRawCertData());
            var matches = CryptographicOperations.FixedTimeEquals(actual, expected);
            if (!matches)
                _logger.LogWarning("LDAP: Serverzertifikat stimmt nicht mit dem konfigurierten SHA-256-Pin überein (tatsächlich: {ActualPin}).", Convert.ToHexString(actual));
            return matches;
        };
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
