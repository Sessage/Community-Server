using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography;

namespace TodoSuite.Server.Services;

/// <summary>
/// Creates authenticated LDAP search connections for every directory feature.
/// Keeping transport fallback, AD credential variants and certificate pinning here
/// prevents login and directory sharing from behaving differently with the same setup.
/// </summary>
public static class LdapDirectoryConnection
{
    public static LdapConnection CreateAndBindSearchConnection(
        ActiveDirectoryOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(options.BindUser))
            return CreateAndBindConnection(options, logger, null, null, allowAdCredentialVariants: false);

        return CreateAndBindConnection(
            options, logger, options.BindUser, options.BindPassword, allowAdCredentialVariants: true);
    }

    public static LdapConnection CreateAndBindConnection(
        ActiveDirectoryOptions options,
        ILogger logger,
        string? username,
        string? password,
        bool allowAdCredentialVariants)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var errors = new List<string>();
        foreach (var credentialVariant in BuildCredentialVariants(options, username, allowAdCredentialVariants))
        {
            foreach (var strategy in BuildConnectionStrategies(options))
            {
                LdapConnection? connection = null;
                try
                {
                    connection = CreateConnection(options, logger, strategy.Port, strategy.UseSsl, strategy.UseStartTls);
                    if (credentialVariant is null)
                        connection.Bind();
                    else
                        connection.Bind(new NetworkCredential(credentialVariant, password));

                    logger.LogInformation(
                        "LDAP: Bind erfolgreich via {Mode} gegen {Server}:{Port}.",
                        strategy.Name, options.Server, strategy.Port);
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

    internal static IEnumerable<string?> BuildCredentialVariants(
        ActiveDirectoryOptions options,
        string? username,
        bool allowAdCredentialVariants)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            yield return null;
            yield break;
        }

        yield return username;
        if (!allowAdCredentialVariants || !LdapDirectoryConfiguration.IsActiveDirectory(options)
            || username.Contains('=') || username.Contains(','))
            yield break;

        var domain = LdapDirectoryConfiguration.GetDomainFromBaseDn(options.BaseDn);
        if (!username.Contains('@') && !string.IsNullOrWhiteSpace(domain))
            yield return $"{username}@{domain}";
        if (!username.Contains('\\') && !string.IsNullOrWhiteSpace(domain))
            yield return $"{domain.Split('.')[0].ToUpperInvariant()}\\{username}";
    }

    internal static IEnumerable<(string Name, int Port, bool UseSsl, bool UseStartTls)>
        BuildConnectionStrategies(ActiveDirectoryOptions options)
    {
        yield return ("konfiguriert", options.Port, options.UseSSL, options.UseStartTls);
        if (!options.EnableAutoFallback)
            yield break;

        var configured = (options.Port, options.UseSSL, options.UseStartTls);
        foreach (var fallback in new[]
                 {
                     (Port: 389, UseSsl: false, UseStartTls: false),
                     (Port: 389, UseSsl: false, UseStartTls: true),
                     (Port: 636, UseSsl: true, UseStartTls: false)
                 }.Where(x => x != configured))
            yield return ("Fallback", fallback.Port, fallback.UseSsl, fallback.UseStartTls);
    }

    private static LdapConnection CreateConnection(
        ActiveDirectoryOptions options,
        ILogger logger,
        int port,
        bool useSsl,
        bool useStartTls)
    {
        if (useSsl && useStartTls)
            throw new InvalidOperationException("UseSSL und UseStartTls dürfen nicht gleichzeitig aktiviert sein.");
        if (string.IsNullOrWhiteSpace(options.Server) || string.IsNullOrWhiteSpace(options.BaseDn))
            throw new InvalidOperationException("Für LDAP müssen Server und BaseDn konfiguriert sein.");

        var connection = new LdapConnection(new LdapDirectoryIdentifier(options.Server, port))
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300))
        };
        connection.SessionOptions.ProtocolVersion = 3;
        ConfigureCertificatePin(connection, options, logger, useSsl || useStartTls);
        connection.SessionOptions.SecureSocketLayer = useSsl;
        if (useStartTls)
            connection.SessionOptions.StartTransportLayerSecurity(null);
        return connection;
    }

    private static void ConfigureCertificatePin(
        LdapConnection connection,
        ActiveDirectoryOptions options,
        ILogger logger,
        bool encryptedTransport)
    {
        if (string.IsNullOrWhiteSpace(options.PinnedServerCertificateSha256))
            return;
        if (!encryptedTransport)
            throw new InvalidOperationException("Ein LDAP-Zertifikat-Pin erfordert UseSSL oder UseStartTls.");

        var normalized = options.PinnedServerCertificateSha256
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
            throw new InvalidOperationException(
                "PinnedServerCertificateSha256 ist kein gültiger hexadezimaler SHA-256-Fingerabdruck.", ex);
        }

        if (expected.Length != SHA256.HashSizeInBytes)
            throw new InvalidOperationException("PinnedServerCertificateSha256 muss genau 32 Bytes enthalten.");

        connection.SessionOptions.VerifyServerCertificate = (_, certificate) =>
        {
            var actual = SHA256.HashData(certificate.GetRawCertData());
            var matches = CryptographicOperations.FixedTimeEquals(actual, expected);
            if (!matches)
                logger.LogWarning(
                    "LDAP: Serverzertifikat stimmt nicht mit dem konfigurierten SHA-256-Pin überein (tatsächlich: {ActualPin}).",
                    Convert.ToHexString(actual));
            return matches;
        };
    }
}
