namespace Klassenbibliothek.Services;

public enum ShareInviteResourceType
{
    List,
    Portfolio
}

public sealed record ShareInviteLink(ShareInviteResourceType ResourceType, Guid ResourceId, string Token);

public static class ShareInviteLinkParser
{
    public static bool TryParse(string? value, out ShareInviteLink? invite)
    {
        invite = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i <= segments.Length - 3; i++)
        {
            if (!segments[i].Equals("share", StringComparison.OrdinalIgnoreCase))
                continue;

            var resourceType = segments[i + 1].ToLowerInvariant() switch
            {
                "list" => ShareInviteResourceType.List,
                "portfolio" => ShareInviteResourceType.Portfolio,
                _ => (ShareInviteResourceType?)null
            };

            if (resourceType is null || i + 3 != segments.Length
                || !Guid.TryParse(segments[i + 2], out var resourceId) || resourceId == Guid.Empty)
                continue;

            string? token;
            try
            {
                token = GetQueryValue(uri.Query, "token");
            }
            catch (UriFormatException)
            {
                return false;
            }
            token = token?.Trim();
            if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
                return false;

            invite = new ShareInviteLink(resourceType.Value, resourceId, token);
            return true;
        }

        return false;
    }

    private static string? GetQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedKey = separator >= 0 ? pair[..separator] : pair;
            if (!Uri.UnescapeDataString(encodedKey.Replace('+', ' ')).Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var encodedValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            return Uri.UnescapeDataString(encodedValue.Replace('+', ' '));
        }

        return null;
    }
}
