using System.Collections.Concurrent;
using System.Security.Cryptography;
using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TodoSuite.Server.Auth;

namespace TodoSuite.Server.Services;

/// <summary>
/// Creates, validates, lists, and revokes personal access tokens for API clients.
/// </summary>
/// <remarks>
/// Tokens are generated with cryptographic randomness and persisted only as hashes. The plaintext
/// token returned by <c>CreateAsync</c> cannot be recovered later.
/// </remarks>
public sealed class PersonalAccessTokenService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration)
{
    public const int MaxTokensPerUser = 100;
    public const int MaxNameLength = 200;
    private const string TokenPrefix = "tsa_";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CreationLocks =
        new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<PersonalAccessTokenItem>> ListAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PersonalAccessTokens
            .AsNoTracking()
            .Where(token => token.UserId == userId)
            .OrderByDescending(token => token.CreatedAtUtc)
            .Select(token => new PersonalAccessTokenItem(
                token.Id,
                token.Name,
                !token.AllowWrite,
                token.CreatedAtUtc,
                token.ExpiresAtUtc,
                token.LastUsedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<CreatedPersonalAccessToken> CreateAsync(
        string userId,
        string? name,
        bool readOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var normalizedName = NormalizeName(name);
        // Count and insert form one logical operation per user. Without this gate, concurrent
        // requests could all observe a count below the configured token limit.
        var creationLock = CreationLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await creationLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var createdAtUtc = DateTime.UtcNow;
            var activeTokenCount = await db.PersonalAccessTokens.CountAsync(
                token => token.UserId == userId && token.ExpiresAtUtc > createdAtUtc,
                cancellationToken);
            if (activeTokenCount >= MaxTokensPerUser)
                throw new PersonalAccessTokenLimitExceededException(MaxTokensPerUser);

            // Persist only a one-way hash. The raw bearer value leaves this method exactly once
            // in CreatedPersonalAccessToken and cannot be reconstructed from the database.
            var rawToken = GenerateRawToken();
            var lifetimeDays = ResolveLifetimeDays(configuration);
            var entity = new PersonalAccessTokenEntity
            {
                UserId = userId,
                Name = normalizedName,
                TokenHash = PersonalAccessTokenAuthHandler.HashToken(rawToken),
                AllowWrite = !readOnly,
                CreatedAtUtc = createdAtUtc,
                ExpiresAtUtc = createdAtUtc.AddDays(lifetimeDays)
            };

            db.PersonalAccessTokens.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            return new CreatedPersonalAccessToken(
                entity.Id,
                entity.Name,
                !entity.AllowWrite,
                rawToken,
                entity.CreatedAtUtc,
                entity.ExpiresAtUtc);
        }
        finally
        {
            creationLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        string userId,
        Guid tokenId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Include the owner in the predicate so a guessed token ID cannot revoke another user's token.
        var token = await db.PersonalAccessTokens
            .FirstOrDefaultAsync(item => item.Id == tokenId && item.UserId == userId, cancellationToken);
        if (token is null)
            return false;

        db.PersonalAccessTokens.Remove(token);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static string NormalizeName(string? name)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("Name darf nicht leer sein.", nameof(name));
        if (normalized.Length > MaxNameLength)
            throw new ArgumentException($"Name darf maximal {MaxNameLength} Zeichen lang sein.", nameof(name));
        return normalized;
    }

    internal static string GenerateRawToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        return TokenPrefix + Convert.ToBase64String(randomBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static int ResolveLifetimeDays(IConfiguration configuration)
    {
        var raw = configuration["PersonalAccessTokens:LifetimeDays"]
                  ?? configuration["PERSONAL_ACCESS_TOKEN_LIFETIME_DAYS"];
        if (string.IsNullOrWhiteSpace(raw))
            return 90;
        if (!int.TryParse(raw, out var configured))
            throw new InvalidOperationException("PersonalAccessTokens:LifetimeDays muss eine ganze Zahl sein.");
        return Math.Clamp(configured, 1, 365);
    }
}

public sealed record PersonalAccessTokenItem(
    Guid Id,
    string Name,
    bool ReadOnly,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? LastUsedAtUtc);

public sealed record CreatedPersonalAccessToken(
    Guid Id,
    string Name,
    bool ReadOnly,
    string Token,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public sealed class PersonalAccessTokenLimitExceededException(int maximum)
    : InvalidOperationException($"Es können maximal {maximum} aktive Zugriffstoken angelegt werden.");
