using System.ComponentModel.DataAnnotations;

namespace Klassenbibliothek.Data;

public enum DirectoryPrincipalType { User = 0, Group = 1 }
public enum DirectoryShareResourceType { List = 0, Portfolio = 1 }

public sealed class DirectoryIdentityEntity
{
    [Key] public string UserId { get; set; } = string.Empty;
    [Required] public string PrincipalId { get; set; } = string.Empty;
    public string UserPrincipalName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string[] GroupIds { get; set; } = [];
    public DateTime RefreshedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DirectoryShareGrantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DirectoryShareResourceType ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public DirectoryPrincipalType PrincipalType { get; set; }
    [Required] public string PrincipalId { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    public string? UserPrincipalName { get; set; }
    public ListRole Role { get; set; } = ListRole.Member;
    [Required] public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
