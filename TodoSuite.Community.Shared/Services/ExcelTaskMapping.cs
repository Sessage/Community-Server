using Klassenbibliothek.Localization;
using System.Globalization;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;

namespace Klassenbibliothek.Services;

public sealed class ExcelFieldMapping
{
    public ExcelFieldMapping() { }
    public ExcelFieldMapping(string key, string name, string hint = "") { Key = key; Name = name; Hint = hint; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hint { get; set; } = "";
    public int Column { get; set; } = -1;
    public string DefaultValue { get; set; } = "";
    [System.Text.Json.Serialization.JsonObjectCreationHandling(System.Text.Json.Serialization.JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> ValueMappings { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ExcelTaskMapping
{
    public static List<ExcelFieldMapping> Fields(TodoListEntity list, bool customFields) =>
    [
        new("Title", SharedText.Get("ExcelImport_FieldTitle")), new("Description", SharedText.Get("ExcelImport_FieldDescription")),
        new("Column", SharedText.Get("ExcelImport_FieldColumn"), string.Join(", ", list.Columns ?? [])),
        new("StartDate", SharedText.Get("ExcelImport_FieldStartDate"), SharedText.Get("ExcelImport_DateHint")),
        new("DueDate", SharedText.Get("ExcelImport_FieldDueDate"), SharedText.Get("ExcelImport_DateHint")),
        new("Done", SharedText.Get("ExcelImport_FieldDone"), SharedText.Get("ExcelImport_BooleanHint")),
        new("IsImportant", SharedText.Get("ExcelImport_FieldIsImportant"), SharedText.Get("ExcelImport_BooleanHint")),
        new("Assignee", SharedText.Get("ExcelImport_FieldAssignee"), SharedText.Get("ExcelImport_AssigneeHint")),
        new("Labels", SharedText.Get("ExcelImport_FieldLabels"), SharedText.Get("ExcelImport_LabelsHint")),
        ..(customFields ? list.CustomFields ?? [] : []).OrderBy(f => f.SortOrder).Select(f =>
            new ExcelFieldMapping(f.Id.ToString(), f.Name + (f.IsRequired ? " *" : ""), f.Type switch
            {
                TodoCustomFieldType.Dropdown or TodoCustomFieldType.MultiSelect => SharedText.Get("ExcelImport_OptionsHint", string.Join("; ", f.Options.Select(o => o.Value))) + (f.Type == TodoCustomFieldType.MultiSelect ? SharedText.Get("ExcelImport_MultipleHint") : ""),
                TodoCustomFieldType.TaskTitleSelect => SharedText.Get("ExcelImport_TaskReferenceHint"),
                TodoCustomFieldType.Date => SharedText.Get("ExcelImport_DateHint"),
                TodoCustomFieldType.Number => SharedText.Get("ExcelImport_NumberHint"),
                TodoCustomFieldType.Checkbox => SharedText.Get("ExcelImport_BooleanHint"),
                _ => SharedText.Get("ExcelImport_TextHint")
            }))
    ];

    public static int FindColumn(string[] headers, ExcelFieldMapping field)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            field.Key, field.Name.TrimEnd(' ', '*')
        };
        if (field.Key is "Title" or "Description" or "Column" or "StartDate" or "DueDate" or "Done" or "IsImportant" or "Assignee" or "Labels")
            foreach (var cultureName in UserLanguagePreferences.SupportedCultures)
                names.Add(SharedText.GetForCulture("ExcelImport_Field" + field.Key,
                    CultureInfo.GetCultureInfo(cultureName)).TrimEnd(' ', '*'));
        return Array.FindIndex(headers, header => names.Contains(header.Trim()));
    }

    public static IReadOnlyList<TodoTaskEntity> Build(ExcelImportSheet sheet, bool date1904,
        IReadOnlyList<ExcelFieldMapping> mappings, TodoListEntity list, bool customFields)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(mappings);
        if (sheet.Rows is null || sheet.Headers is null
            || sheet.Rows.Count is < 1 or > ExcelImportDocument.MaxRows || sheet.Headers.Length is < 1 or > ExcelImportDocument.MaxColumns
            || sheet.Headers.Any(h => h is null || h.Length > ExcelImportDocument.MaxCellLength)
            || sheet.Rows.Any(r => r is null || r.Cells is null || r.Cells.Length > ExcelImportDocument.MaxColumns
                || r.Number is < 1 or > ExcelImportDocument.MaxExcelRowNumber
                || r.Cells.Any(c => c is null || c.Length > ExcelImportDocument.MaxCellLength)))
            throw new ArgumentException(SharedText.Get("ExcelImport_InvalidDimensions"));
        var allowed = Fields(list, customFields).Select(f => f.Key).ToHashSet();
        if (mappings.Any(m => m is null || !allowed.Contains(m.Key) || m.Column < -1 || m.Column >= sheet.Headers.Length)
            || mappings.GroupBy(m => m.Key).Any(g => g.Count() > 1))
            throw new ArgumentException(SharedText.Get("ExcelImport_InvalidMapping"));
        var result = new List<TodoTaskEntity>();
        foreach (var row in sheet.Rows)
        {
            try
            {
                string Value(string key)
                {
                    var mapping = mappings.SingleOrDefault(m => m.Key == key);
                    var value = mapping is { Column: >= 0 } && mapping.Column < row.Cells.Length ? row.Cells[mapping.Column] : "";
                    value = (string.IsNullOrWhiteSpace(value) ? mapping?.DefaultValue ?? "" : value).Trim();
                    if (value.Length > ExcelImportDocument.MaxCellLength || value.Any(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t'))
                        throw new ArgumentException(SharedText.Get("ExcelImport_InvalidValue"));
                    return value;
                }
                var columnMapping = mappings.SingleOrDefault(m => m.Key == "Column");
                var hasSourceColumn = columnMapping is { Column: >= 0 } && columnMapping.Column < row.Cells.Length
                    && !string.IsNullOrWhiteSpace(row.Cells[columnMapping.Column]);
                var task = new TodoTaskEntity
                {
                    Title = TodoTaskInputValidation.NormalizeTitle(Value("Title"), "Titel"),
                    // Treat spreadsheet descriptions as plain text, including any rich-text storage prefix.
                    Description = RichTextContent.FromEditorHtml(System.Net.WebUtility.HtmlEncode(Value("Description")).Replace("\n", "<br>", StringComparison.Ordinal)),
                    Column = ResolveImportColumn(list, hasSourceColumn ? columnMapping : null, Value("Column")),
                    Assignee = TodoTaskInputValidation.ResolveAssignee(list, Value("Assignee"), SharedText.Get("ExcelImport_FieldAssignee")),
                    StartDate = Date(Value("StartDate"), date1904), DueDate = Date(Value("DueDate"), date1904),
                    Done = Boolean(Value("Done")), IsImportant = Boolean(Value("IsImportant"))
                };
                foreach (var labelName in Value("Labels").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var labels = (list.Labels ?? []).Where(l => string.Equals(l.Title, labelName, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (labels.Length != 1) throw new ArgumentException(SharedText.Get("ExcelImport_UnknownLabel", labelName));
                    task.LabelLinks.Add(new() { TaskId = task.Id, LabelId = labels[0].Id });
                }
                foreach (var field in customFields ? list.CustomFields ?? [] : [])
                {
                    var value = Value(field.Id.ToString());
                    if (value.Length == 0)
                    {
                        if (field.IsRequired) throw new ArgumentException(SharedText.Get("ExcelImport_RequiredField", field.Name));
                        continue;
                    }
                    value = field.Type switch
                    {
                        TodoCustomFieldType.Checkbox => Boolean(value).ToString(),
                        TodoCustomFieldType.Date => Date(value, date1904)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        TodoCustomFieldType.Number => Number(value),
                        TodoCustomFieldType.MultiSelect => CustomFieldMultiSelectValues.Serialize(value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
                        _ => value
                    };
                    if (field.IsRequired && field.Type == TodoCustomFieldType.MultiSelect && CustomFieldMultiSelectValues.Parse(value).Count == 0)
                        throw new ArgumentException(SharedText.Get("ExcelImport_RequiredSelection", field.Name));
                    task.CustomFieldValues.Add(new() { FieldId = field.Id, Value = value });
                }
                result.Add(task);
            }
            catch (ArgumentException ex) { throw new ArgumentException(SharedText.Get("ExcelImport_RowError", row.Number, ex.Message), ex); }
        }
        return result;
    }

    private static string ResolveImportColumn(TodoListEntity list, ExcelFieldMapping? mapping, string sourceValue)
    {
        var target = mapping is not null && mapping.ValueMappings.TryGetValue(sourceValue, out var mapped)
            ? mapped : sourceValue;
        // Explicit translations must still refer to a current list column. Never silently
        // replace an unknown source status or an invalid translation with a default.
        if (sourceValue.Length > 0 && string.IsNullOrWhiteSpace(target))
            throw new ArgumentException(SharedText.Get("ExcelImport_MissingStatusTarget", sourceValue));
        try { return TodoTaskInputValidation.ResolveColumn(list, target); }
        catch (ArgumentException)
        {
            throw new ArgumentException(SharedText.Get("ExcelImport_UnknownStatus", sourceValue, string.Join(", ", list.Columns ?? [])));
        }
    }

    private static string Number(string value)
    {
        var culture = value.Contains(',') ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture;
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, culture, out var number))
            throw new ArgumentException(SharedText.Get("ExcelImport_InvalidNumber"));
        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static bool Boolean(string value)
    {
        var normalized = value.ToLowerInvariant();
        if (normalized is "" or "false" or "nein" or "no" or "0") return false;
        if (normalized is "true" or "ja" or "yes" or "1") return true;
        foreach (var cultureName in UserLanguagePreferences.SupportedCultures)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            if (string.Equals(value, SharedText.GetForCulture("Common_Yes", culture), StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, SharedText.GetForCulture("Common_No", culture), StringComparison.OrdinalIgnoreCase)) return false;
        }
        throw new ArgumentException(SharedText.Get("ExcelImport_InvalidBoolean"));
    }

    private static DateTime? Date(string value, bool date1904)
    {
        if (value.Length == 0) return null;
        if (DateTime.TryParseExact(value, ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && double.IsFinite(serial) && serial >= 0 && serial < 2957000)
        {
            try { return DateTime.SpecifyKind(DateTime.FromOADate(serial + (date1904 ? 1462 : 0)), DateTimeKind.Utc); }
            catch (ArgumentException) { }
        }
        throw new ArgumentException(SharedText.Get("ExcelImport_InvalidDate"));
    }
}
