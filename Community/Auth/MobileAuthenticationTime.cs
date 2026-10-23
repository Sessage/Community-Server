using System.Globalization;
using System.Security.Claims;

namespace TodoSuite.Server.Auth;

/// <summary>Distinguishes an interactive login from a background token rotation.</summary>
public static class MobileAuthenticationTime
{
    public const string ClaimType = "auth_time";

    public static void AddLoginClaim(ICollection<Claim> claims, bool isRefresh, DateTimeOffset now,
        DateTimeOffset? originalAuthentication = null)
    {
        var authenticated = isRefresh ? originalAuthentication : now;
        if (authenticated is { } time)
            claims.Add(new Claim(ClaimType, time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
    }

    public static bool IsRecent(ClaimsPrincipal principal, DateTimeOffset now)
        => long.TryParse(principal.FindFirstValue(ClaimType), NumberStyles.None, CultureInfo.InvariantCulture, out var authenticated)
            && authenticated <= now.ToUnixTimeSeconds() && authenticated >= now.ToUnixTimeSeconds() - 300;
}
