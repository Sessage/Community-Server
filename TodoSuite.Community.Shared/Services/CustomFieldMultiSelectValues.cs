using System.Text.Json;

namespace Klassenbibliothek.Services;

public static class CustomFieldMultiSelectValues
{
    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                return (JsonSerializer.Deserialize<List<string>>(trimmed) ?? [])
                    .SelectMany(ParseToken)
                    .Where(option => !string.IsNullOrWhiteSpace(option))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                // Fall back to the tolerant text parser below.
            }
        }

        return trimmed
            .Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Serialize(IEnumerable<string> values)
        => JsonSerializer.Serialize(values
            .Select(Normalize)
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList());

    public static string Toggle(string? value, string option, bool selected)
    {
        var values = Parse(value).ToList();
        values.RemoveAll(existing => string.Equals(existing, option, StringComparison.OrdinalIgnoreCase));
        if (selected)
            values.Add(Normalize(option));
        return Serialize(values);
    }

    private static string Normalize(string? value)
        => (value ?? "").Trim();

    private static IEnumerable<string> ParseToken(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        if (normalized.StartsWith("[", StringComparison.Ordinal))
        {
            List<string>? nested = null;
            try
            {
                nested = JsonSerializer.Deserialize<List<string>>(normalized);
            }
            catch
            {
                // Treat malformed JSON-looking text as a normal option value.
            }

            if (nested is not null)
            {
                foreach (var nestedValue in nested.SelectMany(ParseToken))
                    yield return nestedValue;
                yield break;
            }
        }

        yield return normalized;
    }
}
