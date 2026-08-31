namespace TodoSuite.Server.Auth;

public sealed class JwtTokenOptions
{
    public const string SecurityStampClaimType = "sessage:security_stamp";
    public const int DefaultExpiresMinutes = 120;
    public const int MaxExpiresMinutes = 43_200;

    public required string Key { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public int ExpiresMinutes { get; init; } = DefaultExpiresMinutes;
}
