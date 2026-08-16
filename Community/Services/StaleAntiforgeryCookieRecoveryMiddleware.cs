using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TodoSuite.Server.Services;

/// <summary>
/// Recovers browser form posts from antiforgery cookies that were protected with an
/// obsolete Data Protection key, for example when a page stayed open during an update.
/// </summary>
public sealed class StaleAntiforgeryCookieRecoveryMiddleware(
    RequestDelegate next,
    ILogger<StaleAntiforgeryCookieRecoveryMiddleware> logger)
{
    internal const string RecoveryCookieName = "Sessage.AntiforgeryRecovery";
    private const string AntiforgeryCookiePrefix = ".AspNetCore.Antiforgery.";
    private const string IdentityStatusCookieName = "Identity.StatusMessage";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);

            var validation = context.Features.Get<IAntiforgeryValidationFeature>();
            if (context.Response.StatusCode == StatusCodes.Status400BadRequest
                && validation is { IsValid: false, Error: not null }
                && CanRecover(context))
            {
                LogRecovery(context, validation.Error);
                Recover(context);
            }
        }
        catch (Exception exception) when (ContainsAntiforgeryValidationFailure(exception) && CanRecover(context))
        {
            LogRecovery(context, exception);
            Recover(context);
        }
    }

    private void LogRecovery(HttpContext context, Exception exception) =>
        logger.LogWarning(
            "A stale antiforgery cookie was removed and browser request {Method} {Path} was redirected. TraceIdentifier: {TraceIdentifier}. Reason: {Reason}",
            context.Request.Method,
            context.Request.Path,
            context.TraceIdentifier,
            exception.Message);

    private static bool ContainsAntiforgeryValidationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AntiforgeryValidationException)
                return true;
        }

        return false;
    }

    private static bool CanRecover(HttpContext context)
    {
        if (context.Response.HasStarted
            || HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method)
            || context.Request.Path.StartsWithSegments("/api")
            || context.Request.Cookies.ContainsKey(RecoveryCookieName))
        {
            return false;
        }

        return context.Request.GetTypedHeaders().Accept?.Any(value =>
            string.Equals(value.MediaType.Value, "text/html", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static void Recover(HttpContext context)
    {
        context.Response.Clear();

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            Path = "/",
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps
        };

        foreach (var cookieName in context.Request.Cookies.Keys
                     .Where(name => name.StartsWith(AntiforgeryCookiePrefix, StringComparison.Ordinal)))
        {
            context.Response.Cookies.Delete(cookieName, cookieOptions);
        }

        context.Response.Cookies.Append(
            RecoveryCookieName,
            "1",
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                MaxAge = TimeSpan.FromMinutes(2),
                Path = "/",
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });

        context.Response.Cookies.Append(
            IdentityStatusCookieName,
            "Ihre Sitzung wurde automatisch aktualisiert. Bitte führen Sie die gewünschte Aktion erneut aus.",
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                MaxAge = TimeSpan.FromSeconds(30),
                Path = "/",
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps
            });

        // 303 deliberately converts the failed form POST into a safe GET. Replaying a
        // password or another state-changing form submission would be unsafe.
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = string.Concat(
            context.Request.PathBase,
            context.Request.Path,
            context.Request.QueryString);
    }
}
