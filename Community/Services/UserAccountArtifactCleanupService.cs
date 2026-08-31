using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace TodoSuite.Server.Services;

/// <summary>
/// Removes account-owned artifacts that are not protected by an Identity foreign-key cascade.
/// It is intentionally called only after Identity has successfully deleted the user.
/// </summary>
public sealed class UserAccountArtifactCleanupService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IWebHostEnvironment environment,
    ILogger<UserAccountArtifactCleanupService> logger)
{
    public async Task CleanupAfterUserDeletionAsync(
        string userId,
        string? profilePicturePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var tokens = await db.PersonalAccessTokens
                .Where(token => token.UserId == userId)
                .ToListAsync(cancellationToken);
            if (tokens.Count > 0)
            {
                db.PersonalAccessTokens.RemoveRange(tokens);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not remove personal access tokens for deleted user {UserId}.", userId);
        }

        var fullPath = ResolveProfilePicturePath(profilePicturePath);
        if (fullPath is null || !File.Exists(fullPath))
            return;

        try
        {
            File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove profile picture for deleted user {UserId}.", userId);
        }
    }

    private string? ResolveProfilePicturePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var webRoot = string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? Path.Combine(environment.ContentRootPath, "wwwroot")
            : environment.WebRootPath;
        var root = Path.GetFullPath(Path.Combine(webRoot, "profile-pictures"));
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, relativePath));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }
}
