using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

public class ListInviteEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid ListId { get; set; }

    [Required]
    public string Token { get; set; } = string.Empty;

    public ListRole Role { get; set; } = ListRole.Member;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;

    public bool Revoked { get; set; }

    // ? NEU: Typ
    public ListInviteType Type { get; set; } = ListInviteType.ShareLink;

    // ? NEU: Share-Link Kommentar/Label
    public string? Comment { get; set; }

    // ? NEU: E-Mail Invites optional an E-Mail binden
    public string? InviteEmail { get; set; }

    // ? NEU: Single-use / Nutzungstracking
    public bool SingleUse { get; set; } = false;
    public DateTime? UsedAtUtc { get; set; }

    [ForeignKey(nameof(ListId))]
    public TodoListEntity? List { get; set; }
}
