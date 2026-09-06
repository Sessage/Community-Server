using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace TodoSuite.Server.Services;

/// <summary>
/// Tracks short-lived authentication failures by subject and client address to slow credential attacks.
/// Successful authentication clears the subject bucket; expired entries are pruned opportunistically.
/// </summary>
public sealed class AuthAttemptProtectionService
{
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CleanupAfter = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public AuthBlockStatus Check(HttpContext httpContext, string? subject)
    {
        CleanupExpired();

        var now = DateTimeOffset.UtcNow;
        var ipKey = CreateIpKey(httpContext);
        if (ipKey is not null)
        {
            var ipStatus = CheckKey(ipKey, now);
            if (ipStatus.IsBlocked)
                return ipStatus;
        }

        var subjectKey = CreateSubjectKey(subject);
        return string.IsNullOrWhiteSpace(subjectKey)
            ? AuthBlockStatus.Allowed
            : CheckKey(subjectKey, now);
    }

    public void RecordFailure(HttpContext httpContext, string? subject)
    {
        var now = DateTimeOffset.UtcNow;
        var ipKey = CreateIpKey(httpContext);
        if (ipKey is not null)
            RecordFailure(ipKey, now, isIpKey: true);

        var subjectKey = CreateSubjectKey(subject);
        if (!string.IsNullOrWhiteSpace(subjectKey))
            RecordFailure(subjectKey, now, isIpKey: false);
    }

    public void RecordSuccess(HttpContext httpContext, string? subject)
    {
        // Do not clear the IP-wide bucket here. Otherwise an attacker can reset it by
        // alternating failed attempts with a successful login to another account.
        var subjectKey = CreateSubjectKey(subject);
        if (!string.IsNullOrWhiteSpace(subjectKey))
            _attempts.TryRemove(subjectKey, out _);
    }

    private AuthBlockStatus CheckKey(string key, DateTimeOffset now)
    {
        if (!_attempts.TryGetValue(key, out var state) || state.BlockedUntil <= now)
            return AuthBlockStatus.Allowed;

        return new AuthBlockStatus(true, state.BlockedUntil - now);
    }

    private void RecordFailure(string key, DateTimeOffset now, bool isIpKey)
    {
        _attempts.AddOrUpdate(
            key,
            _ => CreateState(1, now, isIpKey),
            (_, existing) =>
            {
                if (now - existing.WindowStartedAt > FailureWindow)
                    return CreateState(1, now, isIpKey);

                var failures = existing.Failures + 1;
                return CreateState(failures, existing.WindowStartedAt, isIpKey);
            });
    }

    private static AttemptState CreateState(int failures, DateTimeOffset windowStartedAt, bool isIpKey)
    {
        var blockedUntil = GetBlockDuration(failures, isIpKey) is { } duration
            ? DateTimeOffset.UtcNow.Add(duration)
            : DateTimeOffset.MinValue;

        return new AttemptState(failures, windowStartedAt, blockedUntil, DateTimeOffset.UtcNow);
    }

    private static TimeSpan? GetBlockDuration(int failures, bool isIpKey)
    {
        if (isIpKey)
        {
            return failures switch
            {
                >= 50 => TimeSpan.FromHours(12),
                >= 25 => TimeSpan.FromHours(1),
                >= 10 => TimeSpan.FromMinutes(10),
                _ => null
            };
        }

        return failures switch
        {
            >= 20 => TimeSpan.FromHours(12),
            >= 10 => TimeSpan.FromMinutes(30),
            >= 5 => TimeSpan.FromMinutes(5),
            _ => null
        };
    }

    private static string? CreateIpKey(HttpContext httpContext)
    {
        var address = httpContext.Connection.RemoteIpAddress;
        if (address is null)
            return null;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        // An unresolved private/loopback address is commonly the last reverse proxy hop.
        // Bucketing it would let a few failed attempts lock out every client behind that proxy.
        if (IPAddress.IsLoopback(address) || IsPrivateAddress(address))
            return null;

        return $"ip:{address}";
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10
                   || bytes[0] == 127
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
               || (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }

    private static string? CreateSubjectKey(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        // Deliberately independent from the IP so distributed attempts against one account
        // share a limit. Hashing avoids keeping email addresses/user IDs in singleton keys.
        var normalized = subject.Trim().ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"subject:{Convert.ToHexString(hash)}";
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - CleanupAfter;
        foreach (var item in _attempts)
        {
            if (item.Value.LastFailureAt < cutoff)
                _attempts.TryRemove(item.Key, out _);
        }
    }

    private sealed record AttemptState(
        int Failures,
        DateTimeOffset WindowStartedAt,
        DateTimeOffset BlockedUntil,
        DateTimeOffset LastFailureAt);
}

public readonly record struct AuthBlockStatus(bool IsBlocked, TimeSpan RetryAfter)
{
    public static AuthBlockStatus Allowed => new(false, TimeSpan.Zero);
}
