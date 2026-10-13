using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace TodoSuite.Server.Services;

/// <summary>Opt-in metadata tracing for authentication through proxies. Never logs cookie values or form data.</summary>
public sealed class AuthenticationFlowDiagnosticsMiddleware(RequestDelegate next,
    ILogger<AuthenticationFlowDiagnosticsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAuthenticationFlow(context.Request.Path))
        {
            await next(context);
            return;
        }

        var authenticated = context.User.Identity?.IsAuthenticated == true;
        var receivedCookies = string.Join(",", context.Request.Cookies.Keys.Where(IsIdentityCookie).Order(StringComparer.Ordinal));
        await next(context);
        var issuedCookies = string.Join(" | ", context.Response.Headers.SetCookie
            .Select(SummarizeCookie).Where(summary => summary is not null));
        logger.LogInformation(
            "Authentication flow: instance={Machine}:{Process}; method={Method}; path={Path}; scheme={Scheme}; host={Host}; authenticated={Authenticated}; receivedCookieNames={ReceivedCookies}; status={Status}; redirect={Redirect}; issuedCookies={IssuedCookies}; trace={TraceIdentifier}",
            Environment.MachineName, Environment.ProcessId, context.Request.Method,
            context.Request.Path.Value, context.Request.Scheme, context.Request.Host.Value,
            authenticated, receivedCookies, context.Response.StatusCode,
            RedactRedirect(context.Response.Headers.Location.ToString()), issuedCookies, context.TraceIdentifier);
    }

    private static bool IsAuthenticationFlow(PathString path) => path == "/"
        || path.StartsWithSegments("/_blazor")
        || path.StartsWithSegments("/Account/Login")
        || path.StartsWithSegments("/Account/LoginWith2fa")
        || path.StartsWithSegments("/Account/LoginWithRecoveryCode");

    private static bool IsIdentityCookie(string name) => name.StartsWith(".AspNetCore.Identity.", StringComparison.Ordinal)
        || name.StartsWith("Identity.TwoFactor", StringComparison.Ordinal);

    internal static string? SummarizeCookie(string? header)
    {
        if (!SetCookieHeaderValue.TryParse(header, out var cookie) || !IsIdentityCookie(cookie.Name.Value!)) return null;
        return $"{cookie.Name}; Path={cookie.Path}; Domain={cookie.Domain}; Secure={cookie.Secure}; HttpOnly={cookie.HttpOnly}; SameSite={cookie.SameSite}; Persistent={cookie.Expires.HasValue || cookie.MaxAge.HasValue}";
    }

    internal static string RedactRedirect(string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        return location.Split('?', '#')[0];
    }
}
