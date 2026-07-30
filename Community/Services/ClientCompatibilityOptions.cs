namespace TodoSuite.Server.Services;

public sealed class ClientCompatibilityOptions
{
    public string? LatestVersion { get; set; }
    public string? MinSupportedVersion { get; set; }
    public string? UpdateUrl { get; set; }
    public string? Message { get; set; }
}

public sealed record ClientCompatibilityResult(
    string? CurrentVersion,
    string? LatestVersion,
    string? MinSupportedVersion,
    string? UpdateUrl,
    bool UpdateAvailable,
    bool UpdateRequired,
    string Message);
