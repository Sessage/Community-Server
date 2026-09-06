using System.ComponentModel.DataAnnotations;

namespace Klassenbibliothek.Data;

/// <summary>One-time mobile refresh credential. Only its SHA-256 hash is persisted.</summary>
public sealed class MobileRefreshTokenEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    public Guid FamilyId { get; set; } = Guid.NewGuid();

    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string SecurityStamp { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    [MaxLength(64)]
    public string? ReplacementTokenHash { get; set; }
}
