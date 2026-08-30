using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Klassenbibliothek.Data;

namespace TodoSuite.Server.Components.Account
{
    internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
    {
        public const string StatusCookieName = "Identity.StatusMessage";

        private static readonly CookieBuilder StatusCookieBuilder = new()
        {
            SameSite = SameSiteMode.Strict,
            HttpOnly = true,
            IsEssential = true,
            MaxAge = TimeSpan.FromSeconds(5),
        };

        public void RedirectTo(string? uri)
        {
            navigationManager.NavigateTo(GetSafeBaseRelativeUri(uri));
        }

        public void RedirectTo(string uri, Dictionary<string, object?> queryParameters)
        {
            var uriWithoutQuery = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
            var newUri = navigationManager.GetUriWithQueryParameters(uriWithoutQuery, queryParameters);
            RedirectTo(newUri);
        }

        public void RedirectToWithStatus(string uri, string message, HttpContext context)
        {
            context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
            RedirectTo(uri);
        }

        private string CurrentPath => navigationManager.ToAbsoluteUri(navigationManager.Uri).GetLeftPart(UriPartial.Path);

        public void RedirectToCurrentPage() => RedirectTo(CurrentPath);

        public void RedirectToCurrentPageWithStatus(string message, HttpContext context)
            => RedirectToWithStatus(CurrentPath, message, context);

        public void RedirectToInvalidUser(UserManager<ApplicationUser> userManager, HttpContext context)
            => RedirectToWithStatus("Account/InvalidUser", "Error: Unable to load the current user.", context);

        private string GetSafeBaseRelativeUri(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri) || uri.IndexOf('\\') >= 0)
                return "";

            var baseUri = new Uri(navigationManager.BaseUri, UriKind.Absolute);
            if (!Uri.TryCreate(baseUri, uri, out var target)
                || !string.Equals(baseUri.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(baseUri.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                || baseUri.Port != target.Port
                || !IsBelowBasePath(baseUri.AbsolutePath, target.AbsolutePath))
            {
                return "";
            }

            return navigationManager.ToBaseRelativePath(target.AbsoluteUri);
        }

        private static bool IsBelowBasePath(string basePath, string targetPath)
        {
            var normalizedBasePath = basePath.EndsWith('/') ? basePath : $"{basePath}/";
            return targetPath.Equals(normalizedBasePath.TrimEnd('/'), StringComparison.Ordinal)
                || targetPath.StartsWith(normalizedBasePath, StringComparison.Ordinal);
        }
    }
}
