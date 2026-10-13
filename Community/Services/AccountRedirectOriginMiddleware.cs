using Microsoft.AspNetCore.Http;

namespace TodoSuite.Server.Services;

/// <summary>Keeps local account redirects on the browser's public origin after TLS termination.</summary>
public sealed class AccountRedirectOriginMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/Account"))
        {
            context.Response.OnStarting(() =>
            {
                Rewrite(context, "Location");
                Rewrite(context, "blazor-enhanced-nav-redirect-location");
                return Task.CompletedTask;
            });
        }
        return next(context);
    }

    private static void Rewrite(HttpContext context, string header)
    {
        if (context.Response.Headers.TryGetValue(header, out var value) && value.Count == 1)
            context.Response.Headers[header] = ToSameOriginRelative(value.ToString(), context.Request);
    }

    internal static string ToSameOriginRelative(string location, HttpRequest request)
    {
        if (!Uri.TryCreate($"{request.Scheme}://{request.Host.Value}/", UriKind.Absolute, out var origin)
            || !Uri.TryCreate(location, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https")
            || !string.Equals(target.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            || target.Port != origin.Port
            || !string.IsNullOrEmpty(target.UserInfo)) return location;

        // A relative redirect must never become a network-path reference to another host.
        if (Uri.UnescapeDataString(target.AbsolutePath).Replace('\\', '/').StartsWith("//", StringComparison.Ordinal))
            return location;
        return target.PathAndQuery + target.Fragment;
    }
}
