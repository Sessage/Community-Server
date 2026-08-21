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

    public int IntervalMinutes { get; set; } = 15;

    public DateTime? LastImportAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class ListEmailImportedMessageEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConfigurationId { get; set; }
    public ListEmailImportConfigurationEntity? Configuration { get; set; }

    [Required]
    public string FolderName { get; set; } = "INBOX";

    public uint UidValidity { get; set; }
    public uint MessageUid { get; set; }

    public Guid TaskId { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}
