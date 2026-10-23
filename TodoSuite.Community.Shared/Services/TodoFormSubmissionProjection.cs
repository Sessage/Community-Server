using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

/// <summary>Allowlisted form data for clients. Never serializes passwords or workspace graphs.</summary>
public static class TodoFormSubmissionProjection
{
    public static TodoFormEntity Locked(TodoFormEntity form) => new()
    {
        Id = form.Id, ListId = form.ListId, Name = form.Name, Slug = form.Slug,
        PublicationStatus = form.PublicationStatus, MaxSubmissions = form.MaxSubmissions, SubmissionCount = form.SubmissionCount
    };
    public static TodoFormEntity Create(TodoFormEntity form)
    {
        var customIds = form.Fields.Where(field => field.CustomFieldId.HasValue).Select(field => field.CustomFieldId!.Value).ToHashSet();
        return new TodoFormEntity
        {
            Id = form.Id, ListId = form.ListId, Name = form.Name, Description = form.Description,
            SuccessMessage = form.SuccessMessage, Slug = form.Slug, PublicationStatus = form.PublicationStatus,
            MaxSubmissions = form.MaxSubmissions, SubmissionCount = form.SubmissionCount, CapacityReachedText = form.CapacityReachedText,
            BackgroundColor = form.BackgroundColor, ButtonColor = form.ButtonColor,
            AllowAttachments = form.AllowAttachments, AllowedAttachmentTypes = form.AllowedAttachmentTypes,
            Fields = form.Fields.Select(field => new TodoFormFieldEntity
            {
                Id = field.Id, FormId = form.Id, Source = field.Source, StandardField = field.StandardField,
                CustomFieldId = field.CustomFieldId, Label = field.Label, PublicLabel = field.PublicLabel,
                HelpText = field.HelpText, IsRequired = field.IsRequired, Layout = field.Layout, SortOrder = field.SortOrder,
                ValidationRulesJson = field.ValidationRulesJson, ValidationRules = field.ValidationRules
            }).ToList(),
            List = form.List is null ? null : new TodoListEntity
            {
                Id = form.List.Id, Name = form.List.Name,
                Columns = form.Fields.Any(field => field.StandardField == TodoFormStandardField.Column) ? form.List.Columns : [],
                Labels = form.Fields.Any(field => field.StandardField == TodoFormStandardField.Labels)
                    ? form.List.Labels.Select(label => new TodoLabelEntity { Id = label.Id, Title = label.Title }).ToList() : [],
                CustomFields = form.List.CustomFields.Where(field => customIds.Contains(field.Id)).Select(field => new TodoCustomFieldDefinitionEntity
                {
                    Id = field.Id, Name = field.Name, Type = field.Type, IsRequired = field.IsRequired,
                    // The service has already restricted source lists to the submitting user.
                    // Only the option IDs/titles are needed; never include task descriptions or members.
                    SourceTaskListId = field.SourceTaskList?.Id,
                    SourceTaskList = field.SourceTaskList is null ? null : new TodoListEntity
                    {
                        Id = field.SourceTaskList.Id,
                        Tasks = field.SourceTaskList.Tasks.Where(task => task.DeletedAt is null)
                            .Select(task => new TodoTaskEntity { Id = task.Id, Title = task.Title }).ToList()
                    },
                    Options = CustomFieldSelectOptions.GetOptions(field).Select((option, index) => new TodoCustomFieldOptionEntity
                        { FieldId = field.Id, Value = option.Value, SortOrder = index }).ToList()
                }).ToList()
            }
        };
    }
}

public sealed record TodoFormSubmissionPayload(Guid FormId, IReadOnlyList<TodoFormSubmitValue> Values,
    string SubmissionKey, DateTime IssuedAtUtc, string? Honeypot, string? Password);
public sealed record TodoFormPasswordRequest(string? Password);
