using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Klassenbibliothek.Data;

namespace TodoSuite.Server.Services;

/// <summary>Issues and rotates single-use, origin-independent mobile refresh credentials.</summary>
public sealed class MobileRefreshTokenService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration)
{
    private const string Prefix = "tsr_";
    private const int TokenBytes = 64;
    private readonly int _lifetimeDays = ResolveLifetimeDays(configuration);

    public async Task<IssuedMobileRefreshToken> IssueAsync(
        string userId,
        string securityStamp,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var expired = await db.MobileRefreshTokens
            .Where(token => token.UserId == userId && token.ExpiresAtUtc <= now)
            .ToListAsync(ct);
        db.MobileRefreshTokens.RemoveRange(expired);
        var issued = Create(userId, securityStamp, Guid.NewGuid(), now, now.AddDays(_lifetimeDays));
        db.MobileRefreshTokens.Add(issued.Entity);
        await db.SaveChangesAsync(ct);
        return issued.Result;
    }

    public async Task<MobileRefreshTokenCandidate?> FindAsync(string? rawToken, CancellationToken ct = default)
    {
        if (!IsWellFormed(rawToken))
            return null;

        var hash = Hash(rawToken!);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MobileRefreshTokens
            .Where(token => token.TokenHash == hash)
            .Select(token => new MobileRefreshTokenCandidate(
                token.UserId,
                token.SecurityStamp,
                token.ExpiresAtUtc,
                token.RevokedAtUtc))
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);
    }

    public async Task<IssuedMobileRefreshToken?> RotateAsync(
        string rawToken,
        string expectedUserId,
        string currentSecurityStamp,
        CancellationToken ct = default)
    {
        if (!IsWellFormed(rawToken))
            return null;

        var oldHash = Hash(rawToken);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            var existing = await db.MobileRefreshTokens
                .SingleOrDefaultAsync(token => token.TokenHash == oldHash, ct);
            if (existing is null)
                return null;

            var now = DateTime.UtcNow;
            if (existing.RevokedAtUtc is not null)
            {
                // Reuse of an already rotated credential indicates that a token copy may have
                // escaped. Revoke its complete replacement family rather than racing onward.
                var family = await db.MobileRefreshTokens
                    .Where(token => token.FamilyId == existing.FamilyId && token.RevokedAtUtc == null)
                    .ToListAsync(ct);
                foreach (var token in family)
                    token.RevokedAtUtc = now;
                await db.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return null;
            }

            if (existing.ExpiresAtUtc <= now
                || !string.Equals(existing.UserId, expectedUserId, StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(existing.SecurityStamp),
                    Encoding.UTF8.GetBytes(currentSecurityStamp)))
            {
                existing.RevokedAtUtc = now;
                await db.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return null;
            }

            // A family has an absolute lifetime. Rotation must not turn a bounded
            // credential into an indefinitely renewable session.
            var replacement = Create(
                existing.UserId,
                currentSecurityStamp,
                existing.FamilyId,
                now,
                existing.ExpiresAtUtc);
            existing.RevokedAtUtc = now;
            existing.ReplacementTokenHash = replacement.Entity.TokenHash;
            db.MobileRefreshTokens.Add(replacement.Entity);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return replacement.Result;
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }
    }

    public async Task RevokeUserAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var active = await db.MobileRefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAtUtc == null)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var token in active)
            token.RevokedAtUtc = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAsync(string? rawToken, CancellationToken ct = default)
    {
        if (!IsWellFormed(rawToken))
            return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hash = Hash(rawToken!);
        var token = await db.MobileRefreshTokens.SingleOrDefaultAsync(item => item.TokenHash == hash, ct);
        if (token is null || token.RevokedAtUtc is not null)
            return;
        token.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    internal static bool IsWellFormed(string? token)
        => !string.IsNullOrWhiteSpace(token)
           && token.Length is >= 80 and <= 128
           && token.StartsWith(Prefix, StringComparison.Ordinal)
           && token.AsSpan(Prefix.Length).IndexOfAnyExcept(
               "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0;

    internal static int ResolveLifetimeDays(IConfiguration configuration)
        => int.TryParse(configuration["Jwt:RefreshTokenLifetimeDays"], out var days)
            ? Math.Clamp(days, 1, 90)
            : 30;

    private static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static IssuedEntity Create(
        string userId,
        string securityStamp,
        Guid familyId,
        DateTime now,
        DateTime expires)
    {
        var rawToken = Prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        var entity = new MobileRefreshTokenEntity
        {
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(rawToken),
            SecurityStamp = securityStamp,
            CreatedAtUtc = now,
            ExpiresAtUtc = expires
        };
        return new(entity, new IssuedMobileRefreshToken(rawToken, expires));
    }

    private sealed record IssuedEntity(MobileRefreshTokenEntity Entity, IssuedMobileRefreshToken Result);
}

public sealed record IssuedMobileRefreshToken(string Token, DateTime ExpiresAtUtc);
public sealed record MobileRefreshTokenCandidate(
    string UserId,
    string SecurityStamp,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc);
