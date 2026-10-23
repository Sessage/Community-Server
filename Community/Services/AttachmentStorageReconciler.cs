using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace TodoSuite.Server.Services;

/// <summary>Retries orphan removal after metadata deletion and reclaims interrupted upload staging files.</summary>
public sealed class AttachmentStorageReconciler(IDbContextFactory<ApplicationDbContext> factory,
    IWebHostEnvironment environment, ILogger<AttachmentStorageReconciler> logger)
{
    private const long LeaseKey = 6075445457406612318L;
    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var root = Path.Combine(environment.ContentRootPath, "uploads");
        if (!Directory.Exists(root)) return 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        var postgres = db.Database.IsNpgsql();
        var leased = false;
        var removed = 0;
        try
        {
            if (postgres)
            {
                await db.Database.OpenConnectionAsync(ct);
                await using var claim = db.Database.GetDbConnection().CreateCommand();
                claim.CommandText = $"SELECT pg_try_advisory_lock({LeaseKey})";
                leased = Convert.ToBoolean(await claim.ExecuteScalarAsync(ct));
                if (!leased) return 0;
            }
            var cutoff = DateTime.UtcNow.AddDays(-1);
            // Metadata lookup is batched rather than one database request per file. Existing
            // attachments never consume the orphan budget or starve later candidates.
            foreach (var batch in Directory.EnumerateFiles(root).Select(path => new FileInfo(path))
                .Where(file => file.LastWriteTimeUtc < cutoff && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .Chunk(200))
            {
                var ids = batch.Select(file => Guid.TryParseExact(file.Name, "N", out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty).ToArray();
                var referenced = (await db.TodoAttachments.AsNoTracking().Where(attachment => ids.Contains(attachment.Id))
                    .Select(attachment => attachment.Id).ToListAsync(ct)).ToHashSet();
                foreach (var file in batch)
                {
                ct.ThrowIfCancellationRequested();
                var staged = file.Name.StartsWith(".upload-", StringComparison.Ordinal) && file.Name.EndsWith(".tmp", StringComparison.Ordinal);
                var final = Guid.TryParseExact(file.Name, "N", out var id);
                if (!staged && !final) continue;
                if (final && referenced.Contains(id)) continue;
                try
                {
                    // Deny an active writer, but permit deletion of our own opened handle
                    // on Windows. Recent files and all files with metadata remain intact.
                    using var lease = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (File.GetLastWriteTimeUtc(file.FullName) >= cutoff) continue;
                    File.Delete(file.FullName);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning(ex, "Anhangsdatei konnte nicht bereinigt werden; sie bleibt für einen späteren Versuch erhalten. File={File}", file.Name); }
                if (removed >= 1000) return removed;
                }
            }
        }
        finally
        {
            if (leased)
            {
                await using var release = db.Database.GetDbConnection().CreateCommand();
                release.CommandText = $"SELECT pg_advisory_unlock({LeaseKey})";
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        return removed;
    }
}
