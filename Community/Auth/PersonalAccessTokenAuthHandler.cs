using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Klassenbibliothek.Data;

namespace TodoSuite.Server.Auth;

public class PersonalAccessTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    UserManager<ApplicationUser> userManager)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PersonalAccessToken";
    private const string TokenPrefix = "tsa_";
    private const int EncodedTokenLength = 43;
    private static readonly TimeSpan LastUsedUpdateInterval = TimeSpan.FromMinutes(10);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var rawToken = authHeader["Bearer ".Length..].Trim();
        if (!IsWellFormedToken(rawToken))
            return AuthenticateResult.NoResult();

        var hash = HashToken(rawToken);
        var cancellationToken = Context.RequestAborted;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var pat = await db.PersonalAccessTokens
            .AsTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (pat is null)
            return AuthenticateResult.Fail("Ungültiger Zugriffstoken.");

        var now = DateTime.UtcNow;
        if (pat.ExpiresAtUtc <= now)
            return AuthenticateResult.Fail("Der Zugriffstoken ist abgelaufen.");

        var user = await userManager.FindByIdAsync(pat.UserId);
        if (user is null)
            return AuthenticateResult.Fail("Benutzer nicht gefunden.");

        if (await userManager.IsLockedOutAsync(user))
            return AuthenticateResult.Fail("Das Benutzerkonto ist gesperrt.");

        if (pat.LastUsedAtUtc is null || now - pat.LastUsedAtUtc.Value >= LastUsedUpdateInterval)
        {
            pat.LastUsedAtUtc = now;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                Logger.LogWarning(ex, "Could not update LastUsedAtUtc for personal access token {TokenId}.", pat.Id);
            }
        }

        var isAdmin = await userManager.IsInRoleAsync(user, "Admin");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new("pat:read", "true")
        };
        if (pat.AllowWrite)
            claims.Add(new Claim("pat:write", "true"));
        if (isAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    internal static bool IsWellFormedToken(string rawToken)
    {
        if (rawToken.Length != TokenPrefix.Length + EncodedTokenLength
            || !rawToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return rawToken.AsSpan(TokenPrefix.Length).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0;
    }

    public static string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
