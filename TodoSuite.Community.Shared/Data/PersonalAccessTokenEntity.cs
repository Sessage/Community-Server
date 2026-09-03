using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

/// <summary>Persisted metadata and one-way hash for a personal access token; never contains the raw token.</summary>
public class PersonalAccessTokenEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256-Hash des eigentlichen Token-Werts (tsa_…).</summary>
    [Required]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(90);

    public bool AllowWrite { get; set; } = true;

    public DateTime? LastUsedAtUtc { get; set; }
}
