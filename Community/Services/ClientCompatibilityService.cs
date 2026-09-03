using Microsoft.Extensions.Options;

namespace TodoSuite.Server.Services;

public sealed class ClientCompatibilityService(IOptions<ClientCompatibilityOptions> options)
{
    private readonly ClientCompatibilityOptions _options = options.Value;

    public ClientCompatibilityResult Check(string? currentVersion)
    {
        var latestVersion = NormalizeVersion(_options.LatestVersion);
        var minSupportedVersion = NormalizeVersion(_options.MinSupportedVersion);
        var clientVersion = NormalizeVersion(currentVersion);

        var updateRequired = minSupportedVersion is not null
            && (clientVersion is null || CompareVersions(clientVersion, minSupportedVersion) < 0);

        var updateAvailable = !updateRequired
            && latestVersion is not null
            && clientVersion is not null
            && CompareVersions(clientVersion, latestVersion) < 0;

        var message = string.IsNullOrWhiteSpace(_options.Message)
            ? "Diese App-Version ist nicht mehr kompatibel. Bitte aktualisieren."
            : _options.Message.Trim();

        return new ClientCompatibilityResult(
            clientVersion,
            latestVersion,
            minSupportedVersion,
            string.IsNullOrWhiteSpace(_options.UpdateUrl) ? null : _options.UpdateUrl.Trim(),
            updateAvailable,
            updateRequired,
            message);
    }

    public static bool IsValidConfiguration(ClientCompatibilityOptions options)
    {
        if (options is null
            || NormalizeVersion(options.LatestVersion, allowEmpty: true, out var latest) is false
            || NormalizeVersion(options.MinSupportedVersion, allowEmpty: true, out var minimum) is false)
            return false;

        if (latest is not null && minimum is not null && CompareVersions(latest, minimum) < 0)
            return false;
        if (!string.IsNullOrWhiteSpace(options.UpdateUrl)
            && (!Uri.TryCreate(options.UpdateUrl.Trim(), UriKind.Absolute, out var updateUri)
                || updateUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(updateUri.UserInfo)))
            return false;
        return options.Message?.Length is not > 500;
    }

    private static string? NormalizeVersion(string? value)
    {
        return NormalizeVersion(value, allowEmpty: true, out var normalized) ? normalized : null;
    }

    private static bool NormalizeVersion(string? value, bool allowEmpty, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return allowEmpty;

        var version = value.Trim();
        var separatorIndex = version.IndexOfAny(['+', '-']);
        var core = separatorIndex > 0 ? version[..separatorIndex] : version;
        var parts = core.Split('.', StringSplitOptions.None);
        if (parts.Length is < 1 or > 4
            || parts.Any(part => part.Length == 0
                                 || !part.All(char.IsAsciiDigit)
                                 || !int.TryParse(part, out _)))
            return false;

        normalized = core;
        return true;
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        var max = Math.Max(leftParts.Length, rightParts.Length);

        for (var i = 0; i < max; i++)
        {
            var leftPart = i < leftParts.Length ? leftParts[i] : 0;
            var rightPart = i < rightParts.Length ? rightParts[i] : 0;
            var compare = leftPart.CompareTo(rightPart);
            if (compare != 0)
                return compare;
        }

        return 0;
    }

    private static int[] ParseVersionParts(string version)
        => version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .ToArray();
}
