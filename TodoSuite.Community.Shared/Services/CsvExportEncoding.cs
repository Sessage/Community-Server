namespace Klassenbibliothek.Services;

/// <summary>Encodes untrusted values as inert CSV cells for spreadsheet applications.</summary>
public static class CsvExportEncoding
{
    public static string Escape(string? value)
    {
        if (value is null) return string.Empty;

        // Excel and similar applications may execute these prefixes as formulas even when
        // the field is quoted. An apostrophe makes the exported value explicit text.
        var safeValue = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'
            ? $"'{value}"
            : value;

        return safeValue.Contains(',') || safeValue.Contains('"') || safeValue.Contains('\n') || safeValue.Contains('\r')
            ? $"\"{safeValue.Replace("\"", "\"\"")}\""
            : safeValue;
    }
}
