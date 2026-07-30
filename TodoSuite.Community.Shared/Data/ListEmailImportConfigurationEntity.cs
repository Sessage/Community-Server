using System.ComponentModel.DataAnnotations;

namespace Klassenbibliothek.Data;

public class ListEmailImportConfigurationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ListId { get; set; }
    public TodoListEntity? List { get; set; }

    [Required]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 993;

    public bool UseSsl { get; set; } = true;

    [Required]
    public string UserName { get; set; } = string.Empty;

    [Required]
    public string EncryptedPassword { get; set; } = string.Empty;

    [Required]
    public string FolderName { get; set; } = "INBOX";

    public string? TargetColumn { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime? LastImportAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
