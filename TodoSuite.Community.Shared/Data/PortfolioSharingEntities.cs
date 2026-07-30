using System.ComponentModel.DataAnnotations;

namespace Klassenbibliothek.Data;

public class PortfolioParticipantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PortfolioGroupId { get; set; }
    [Required] public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public ListRole Role { get; set; } = ListRole.Member;
    public ListRole? DirectRole { get; set; }
    public ListRole? DirectoryRole { get; set; }
    public bool DirectInvitationPending { get; set; }
    public bool InvitationPending { get; set; } = true;

    public void RecalculateEffectiveAccess()
    {
        var roles = new[] { DirectRole, DirectoryRole }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        Role = roles.Length == 0 ? ListRole.Observer : (ListRole)roles.Min(x => (int)x);
        InvitationPending = DirectRole.HasValue && DirectInvitationPending && DirectoryRole is null;
    }
}

public class PortfolioInviteEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PortfolioGroupId { get; set; }
    [Required] public string Token { get; set; } = string.Empty;
    public string InviteEmail { get; set; } = string.Empty;
    public ListRole Role { get; set; } = ListRole.Member;
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }
    public bool Revoked { get; set; }
    public string? Comment { get; set; }
}
