using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Klassenbibliothek.Data;

public enum TodoFormPublicationStatus
{
    Draft = 0,
    Public = 1,
    Private = 2,
    PasswordProtected = 3
}

public enum TodoFormFieldSource
{
    Standard = 0,
    Custom = 1
}

public enum TodoFormStandardField
{
    Title = 0,
    Description = 1,
    Column = 2,
    StartDate = 4,
    DueDate = 5,
    Assignee = 6,
    IsImportant = 7,
    Labels = 8,
    Attachments = 9
}

public enum TodoFormFieldLayout
{
    Full = 0,
    Half = 1,
    Wide70 = 2,
    Narrow30 = 3
}

public enum TodoFormFieldValidationType
{
    Number = 0,
    Integer = 1,
    Email = 2,
    Iban = 3,
    DateAfter = 4,
    DateBefore = 5,
    DateOnOrAfter = 6,
    DateOnOrBefore = 7,
    MinLength = 8,
    MaxLength = 9,
    Regex = 10
}

[Flags]
public enum TodoFormAttachmentType
{
    None = 0,
    Documents = 1,
    Images = 2,
    Archives = 4,
    Audio = 8,
    Video = 16,
    All = Documents | Images | Archives | Audio | Video
}

public class TodoFormFieldValidationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TodoFormFieldValidationType Type { get; set; } = TodoFormFieldValidationType.Email;
    public string? CompareValue { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Public task-entry form definition. Publication state controls whether anonymous submission is possible.
/// </summary>
public class TodoFormEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(List))]
    public Guid ListId { get; set; }

    public TodoListEntity? List { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? SuccessMessage { get; set; }

    [Required]
    public string Slug { get; set; } = string.Empty;

    public TodoFormPublicationStatus PublicationStatus { get; set; } = TodoFormPublicationStatus.Draft;

    public string? PasswordSalt { get; set; }

    public string? PasswordHash { get; set; }

    public int? MaxSubmissions { get; set; }

    public string? CapacityReachedText { get; set; }

    public string? BackgroundColor { get; set; }

    public string? ButtonColor { get; set; }

    public bool AllowAttachments { get; set; }

    public TodoFormAttachmentType AllowedAttachmentTypes { get; set; } = TodoFormAttachmentType.None;

    [NotMapped]
    public int SubmissionCount { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<TodoFormFieldEntity> Fields { get; set; } = new();
}

public class TodoFormFieldEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Form))]
    public Guid FormId { get; set; }

    public TodoFormEntity? Form { get; set; }

    public TodoFormFieldSource Source { get; set; }

    public TodoFormStandardField? StandardField { get; set; }

    public Guid? CustomFieldId { get; set; }

    public string Label { get; set; } = string.Empty;

    public string? PublicLabel { get; set; }

    public string? HelpText { get; set; }

    public bool IsRequired { get; set; }

    public TodoFormFieldLayout Layout { get; set; } = TodoFormFieldLayout.Full;

    public string? ValidationRulesJson { get; set; }

    [NotMapped]
    public List<TodoFormFieldValidationRule> ValidationRules { get; set; } = new();

    public int SortOrder { get; set; }

    public void LoadValidationRules()
    {
        if (string.IsNullOrWhiteSpace(ValidationRulesJson))
        {
            ValidationRules = new();
            return;
        }

        try
        {
            ValidationRules = JsonSerializer.Deserialize<List<TodoFormFieldValidationRule>>(ValidationRulesJson) ?? new();
        }
        catch
        {
            // Ungültige Alt-/Importdaten werden nicht in ausführbare Validierungsregeln
            // umgewandelt. Die Servervalidierung des eigentlichen Formulars bleibt davon unberührt.
            ValidationRules = new();
        }
    }

    public void StoreValidationRules()
    {
        // Nur Regeln mit einer sichtbaren Fehlermeldung werden gespeichert. Werte und Meldungen
        // werden normalisiert, damit Editor-Roundtrips deterministisch bleiben.
        var rules = ValidationRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Message))
            .Select(r => new TodoFormFieldValidationRule
            {
                Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
                Type = r.Type,
                CompareValue = string.IsNullOrWhiteSpace(r.CompareValue) ? null : r.CompareValue.Trim(),
                Message = r.Message.Trim()
            })
            .ToList();

        ValidationRulesJson = rules.Count == 0 ? null : JsonSerializer.Serialize(rules);
        ValidationRules = rules;
    }
}

public class TodoFormSubmissionKeyEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Form))]
    public Guid FormId { get; set; }

    public TodoFormEntity? Form { get; set; }

    // Pro Formular eindeutig indizierter Idempotenzschlüssel: wiederholte Browser-/Mobile-
    // Übertragungen erzeugen nach einem Timeout nicht versehentlich eine zweite Aufgabe.
    [Required]
    public string SubmissionKey { get; set; } = string.Empty;

    public string? IpHash { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? TaskId { get; set; }
}

public record TodoFormSubmitValue(string FieldKey, string? Value);

public record TodoFormSubmitAttachment(
    string FileName,
    string? ContentType,
    long Size,
    Func<CancellationToken, Stream> OpenReadStream);

public record TodoFormSubmitRequest(
    Guid FormId,
    IReadOnlyList<TodoFormSubmitValue> Values,
    IReadOnlyList<TodoFormSubmitAttachment> Attachments,
    string SubmissionKey,
    DateTime IssuedAtUtc,
    string? Honeypot,
    string? Password,
    string? RemoteAddress);

public record TodoFormSubmitResult(bool Success, string Message, Guid? TaskId = null);
