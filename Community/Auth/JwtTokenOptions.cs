namespace TodoSuite.Server.Auth;

public sealed class JwtTokenOptions
{
    public required string Key { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public int ExpiresMinutes { get; init; } = 120;
}
