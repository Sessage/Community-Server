using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Klassenbibliothek.Data;

public class ListParticipantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public string Email { get; set; } = string.Empty;

    public bool InvitationPending { get; set; }

    // NEU: Rolle
    public ListRole Role { get; set; } = ListRole.Member;

    /// <summary>Explizit auf dieser Liste erteilte Rolle.</summary>
    public ListRole? DirectRole { get; set; }

    /// <summary>Ob eine explizite E-Mail-Einladung noch nicht angenommen wurde.</summary>
    public bool DirectInvitationPending { get; set; }

    /// <summary>Vom zugeordneten Portfolio vererbte Rolle.</summary>
    public ListRole? PortfolioRole { get; set; }

    /// <summary>Über eine Enterprise-Verzeichnisfreigabe erteilte Rolle.</summary>
    public ListRole? DirectoryRole { get; set; }

    // Optional: wenn eingeloggter User bereits bekannt/verknüpft ist
    public string? UserId { get; set; }

    /// <summary>Quelle eines aus einer Portfolio-Freigabe abgeleiteten Listenzugriffs.</summary>
    public Guid? SourcePortfolioGroupId { get; set; }

    public void RecalculateEffectiveAccess()
    {
        var roles = new[] { DirectRole, PortfolioRole, DirectoryRole }
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        Role = roles.Length == 0 ? ListRole.Observer : (ListRole)roles.Min(x => (int)x);
        InvitationPending = DirectRole.HasValue && DirectInvitationPending && PortfolioRole is null && DirectoryRole is null;
    }

    [ForeignKey(nameof(List))]
    public Guid ListId { get; set; }

    public TodoListEntity? List { get; set; }
}
