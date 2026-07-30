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
            _options.UpdateUrl,
            updateAvailable,
            updateRequired,
            message);
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var version = value.Trim();
        var separatorIndex = version.IndexOfAny(['+', '-']);
        return separatorIndex > 0 ? version[..separatorIndex] : version;
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
