using System.Globalization;
using Klassenbibliothek.Data;
using Klassenbibliothek.Services;

namespace TodoSuite.Server.Services;

public sealed class ExcelFieldMapping(string key, string name, string hint = "")
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Hint { get; } = hint;
    public int Column { get; set; } = -1;
    public string DefaultValue { get; set; } = "";
    public Dictionary<string, string> ValueMappings { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ExcelTaskMapping
{
    public static List<ExcelFieldMapping> Fields(TodoListEntity list, bool customFields) =>
    [
        new("Title", "Titel *"), new("Description", "Beschreibung"),
        new("Column", "Status / Listenspalte", string.Join(", ", list.Columns ?? [])),
        new("StartDate", "Startdatum", "TT.MM.JJJJ oder JJJJ-MM-TT"),
        new("DueDate", "Fälligkeitsdatum", "TT.MM.JJJJ oder JJJJ-MM-TT"),
        new("Done", "Erledigt", "Ja/Nein, true/false oder 1/0"),
        new("IsImportant", "Wichtig", "Ja/Nein, true/false oder 1/0"),
        new("Assignee", "Bearbeiter", "Benutzer-ID oder E-Mail eines angenommenen Listenteilnehmers"),
        new("Labels", "Labels", "Vorhandene Labelnamen, mit ; getrennt"),
        ..(customFields ? list.CustomFields ?? [] : []).OrderBy(f => f.SortOrder).Select(f =>
            new ExcelFieldMapping(f.Id.ToString(), f.Name + (f.IsRequired ? " *" : ""), f.Type switch
            {
                TodoCustomFieldType.Dropdown or TodoCustomFieldType.MultiSelect => "Auswahl: " + string.Join("; ", f.Options.Select(o => o.Value)) + (f.Type == TodoCustomFieldType.MultiSelect ? " (mehrere mit ; trennen)" : ""),
                TodoCustomFieldType.TaskTitleSelect => "ID einer vorhandenen Aufgabe aus der verknüpften Liste",
                TodoCustomFieldType.Date => "TT.MM.JJJJ oder JJJJ-MM-TT",
                TodoCustomFieldType.Number => "Zahl, z. B. 12,5",
                TodoCustomFieldType.Checkbox => "Ja/Nein, true/false oder 1/0",
                _ => "Text"
            }))
    ];

    public static IReadOnlyList<TodoTaskEntity> Build(ExcelImportSheet sheet, bool date1904,
        IReadOnlyList<ExcelFieldMapping> mappings, TodoListEntity list, bool customFields)
    {
        if (sheet.Rows.Count is < 1 or > ExcelImportDocument.MaxRows || sheet.Headers.Length > ExcelImportDocument.MaxColumns)
            throw new ArgumentException("Ungültige Anzahl an Zeilen oder Spalten.");
        var allowed = Fields(list, customFields).Select(f => f.Key).ToHashSet();
        if (mappings.Any(m => !allowed.Contains(m.Key) || m.Column < -1 || m.Column >= sheet.Headers.Length)
            || mappings.GroupBy(m => m.Key).Any(g => g.Count() > 1))
            throw new ArgumentException("Die Feldzuordnung ist ungültig oder nicht mehr aktuell.");
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
                        throw new ArgumentException("Unzulässiger Feldinhalt oder mehr als 10000 Zeichen.");
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
                    Assignee = TodoTaskInputValidation.ResolveAssignee(list, Value("Assignee"), "Bearbeiter"),
                    StartDate = Date(Value("StartDate"), date1904), DueDate = Date(Value("DueDate"), date1904),
                    Done = Boolean(Value("Done")), IsImportant = Boolean(Value("IsImportant"))
                };
                foreach (var labelName in Value("Labels").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var labels = (list.Labels ?? []).Where(l => string.Equals(l.Title, labelName, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (labels.Length != 1) throw new ArgumentException($"Das Label „{labelName}“ ist unbekannt oder nicht eindeutig.");
                    task.LabelLinks.Add(new() { TaskId = task.Id, LabelId = labels[0].Id });
                }
                foreach (var field in customFields ? list.CustomFields ?? [] : [])
                {
                    var value = Value(field.Id.ToString());
                    if (value.Length == 0)
                    {
                        if (field.IsRequired) throw new ArgumentException($"Das Pflichtfeld „{field.Name}“ fehlt.");
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
                        throw new ArgumentException($"Das Pflichtfeld „{field.Name}“ benötigt mindestens eine Auswahl.");
                    task.CustomFieldValues.Add(new() { FieldId = field.Id, Value = value });
                }
                result.Add(task);
            }
            catch (ArgumentException ex) { throw new ArgumentException($"Excel-Zeile {row.Number}: {ex.Message}", ex); }
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
            throw new ArgumentException($"Für den Status „{sourceValue}“ fehlt die Zielspalte. Bitte unter „Statuswerte zuordnen“ eine Listenspalte auswählen.");
        try { return TodoTaskInputValidation.ResolveColumn(list, target); }
        catch (ArgumentException)
        {
            throw new ArgumentException($"Der Status „{sourceValue}“ passt zu keiner Listenspalte. Erlaubt sind: {string.Join(", ", list.Columns ?? [])}. Bitte unter „Statuswerte zuordnen“ die Zielspalte auswählen oder bei „Status / Listenspalte“ die Excel-Zuordnung entfernen und einen festen Standardwert wählen.");
        }
    }

    private static string Number(string value)
    {
        var culture = value.Contains(',') ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture;
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, culture, out var number))
            throw new ArgumentException("Ungültige Zahl (Tausendertrennzeichen bitte entfernen).");
        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static bool Boolean(string value) => value.ToLowerInvariant() switch
    {
        "" or "false" or "nein" or "no" or "0" => false,
        "true" or "ja" or "yes" or "1" => true,
        _ => throw new ArgumentException("Ungültiger Ja/Nein-Wert.")
    };

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
        throw new ArgumentException("Ungültiges Datum. Bitte TT.MM.JJJJ oder JJJJ-MM-TT verwenden.");
    }
}
