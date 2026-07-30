using System.Collections.Concurrent;

namespace TodoSuite.Server.Services;

public sealed class AuthAttemptProtectionService
{
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CleanupAfter = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public AuthBlockStatus Check(HttpContext httpContext, string? subject)
    {
        CleanupExpired();

        var now = DateTimeOffset.UtcNow;
        var ipStatus = CheckKey(CreateIpKey(httpContext), now);
        if (ipStatus.IsBlocked)
            return ipStatus;

        var subjectKey = CreateSubjectKey(httpContext, subject);
        return string.IsNullOrWhiteSpace(subjectKey)
            ? AuthBlockStatus.Allowed
            : CheckKey(subjectKey, now);
    }

    public void RecordFailure(HttpContext httpContext, string? subject)
    {
        var now = DateTimeOffset.UtcNow;
        RecordFailure(CreateIpKey(httpContext), now, isIpKey: true);

        var subjectKey = CreateSubjectKey(httpContext, subject);
        if (!string.IsNullOrWhiteSpace(subjectKey))
            RecordFailure(subjectKey, now, isIpKey: false);
    }

    public void RecordSuccess(HttpContext httpContext, string? subject)
    {
        _attempts.TryRemove(CreateIpKey(httpContext), out _);

        var subjectKey = CreateSubjectKey(httpContext, subject);
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

    private static string CreateIpKey(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        return $"ip:{(string.IsNullOrWhiteSpace(ip) ? "unknown" : ip)}";
    }

    private static string? CreateSubjectKey(HttpContext httpContext, string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"subject:{ip}:{subject.Trim().ToUpperInvariant()}";
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
