using System.Globalization;
using System.Text.RegularExpressions;
using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

/// <summary>
/// Parses deliberately small, unambiguous quick-add shortcuts without silently discarding
/// unknown input. Only tokens that can be mapped to the current list are removed from the title.
/// </summary>
public static partial class SmartTaskInputParser
{
    public static SmartTaskInputResult Parse(
        string? input,
        DateTime today,
        IEnumerable<TodoLabelEntity>? labels = null,
        IEnumerable<ListParticipantEntity>? participants = null)
    {
        var title = (input ?? string.Empty).Trim();
        var dueDate = default(DateTime?);
        var important = false;
        string? assigneeUserId = null;
        var labelIds = new List<Guid>();

        title = ReplaceRecognizedMatches(PriorityToken(), title, _ =>
        {
            important = true;
            return true;
        });

        var knownLabels = (labels ?? [])
            .Where(label => !string.IsNullOrWhiteSpace(label.Title))
            .GroupBy(label => NormalizeAlias(label.Title), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        title = ReplaceRecognizedMatches(LabelToken(), title, match =>
        {
            if (!knownLabels.TryGetValue(NormalizeAlias(match.Groups["alias"].Value), out var label))
                return false;

            if (!labelIds.Contains(label.Id))
                labelIds.Add(label.Id);
            return true;
        });

        var acceptedParticipants = (participants ?? [])
            .Where(participant => !participant.InvitationPending && !string.IsNullOrWhiteSpace(participant.UserId))
            .ToList();
        title = ReplaceRecognizedMatches(AssigneeToken(), title, match =>
        {
            var alias = NormalizeAlias(match.Groups["alias"].Value);
            var candidates = acceptedParticipants
                .Where(participant => GetParticipantAliases(participant).Contains(alias, StringComparer.OrdinalIgnoreCase))
                .Select(participant => participant.UserId!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count != 1)
                return false;

            assigneeUserId = candidates[0];
            return true;
        });

        title = ReplaceRecognizedMatches(RelativeDateToken(), title, match =>
        {
            if (dueDate.HasValue)
                return false;

            dueDate = match.Groups["dayAfterTomorrow"].Success
                ? today.Date.AddDays(2)
                : match.Groups["tomorrow"].Success
                    ? today.Date.AddDays(1)
                    : today.Date;
            return true;
        });

        title = ReplaceRecognizedMatches(NextWeekdayToken(), title, match =>
        {
            if (dueDate.HasValue || !TryParseWeekday(match.Groups["weekday"].Value, out var weekday))
                return false;

            var daysAhead = ((int)weekday - (int)today.DayOfWeek + 7) % 7;
            dueDate = today.Date.AddDays(daysAhead == 0 ? 7 : daysAhead);
            return true;
        });

        title = ReplaceRecognizedMatches(IsoDateToken(), title, match =>
        {
            if (dueDate.HasValue
                || !DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return false;

            dueDate = parsed.Date;
            return true;
        });

        title = ReplaceRecognizedMatches(NumericDateToken(), title, match =>
        {
            if (dueDate.HasValue || !TryParseNumericDate(match.Groups["date"].Value, today, out var parsed))
                return false;

            dueDate = parsed;
            return true;
        });

        return new SmartTaskInputResult(
            NormalizeTitleWhitespace(title),
            dueDate,
            important,
            assigneeUserId,
            labelIds);
    }

    private static string ReplaceRecognizedMatches(Regex regex, string input, Func<Match, bool> consume)
        => regex.Replace(input, match => consume(match) ? " " : match.Value);

    private static IEnumerable<string> GetParticipantAliases(ListParticipantEntity participant)
    {
        var displayName = NormalizeAlias(participant.DisplayName);
        if (displayName.Length > 0)
        {
            yield return displayName;
            foreach (var part in displayName.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return part;
        }

        var email = (participant.Email ?? string.Empty).Trim();
        var at = email.IndexOf('@');
        if (at > 0)
            yield return NormalizeAlias(email[..at]);
    }

    private static string NormalizeAlias(string? value)
        => Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s._]+", "-");

    private static string NormalizeTitleWhitespace(string value)
        => Whitespace().Replace(value, " ").Trim(' ', ',', ';');

    private static bool TryParseNumericDate(string value, DateTime today, out DateTime result)
    {
        var formats = new[] { "d.M.yyyy", "d.M.yy", "d/M/yyyy", "d/M/yy", "d.M.", "d.M", "d/M" };
        foreach (var format in formats)
        {
            if (!DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                continue;

            var hasYear = format.Contains('y');
            result = hasYear
                ? parsed.Date
                : new DateTime(today.Year, parsed.Month, parsed.Day);
            if (!hasYear && result < today.Date)
                result = result.AddYears(1);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseWeekday(string value, out DayOfWeek weekday)
    {
        var normalized = value.Trim().ToLowerInvariant();
        weekday = normalized switch
        {
            "montag" or "monday" => DayOfWeek.Monday,
            "dienstag" or "tuesday" => DayOfWeek.Tuesday,
            "mittwoch" or "wednesday" => DayOfWeek.Wednesday,
            "donnerstag" or "thursday" => DayOfWeek.Thursday,
            "freitag" or "friday" => DayOfWeek.Friday,
            "samstag" or "saturday" => DayOfWeek.Saturday,
            "sonntag" or "sunday" => DayOfWeek.Sunday,
            _ => default
        };
        return normalized is "montag" or "monday" or "dienstag" or "tuesday" or "mittwoch" or "wednesday"
            or "donnerstag" or "thursday" or "freitag" or "friday" or "samstag" or "saturday"
            or "sonntag" or "sunday";
    }

    [GeneratedRegex(@"(?<!\S)!(?:high|urgent|hoch|wichtig)(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PriorityToken();

    [GeneratedRegex(@"(?<!\S)#(?<alias>[\p{L}\p{N}][\p{L}\p{N}._-]*)(?!\S)", RegexOptions.CultureInvariant)]
    private static partial Regex LabelToken();

    [GeneratedRegex(@"(?<!\S)@(?<alias>[\p{L}\p{N}][\p{L}\p{N}._-]*)(?!\S)", RegexOptions.CultureInvariant)]
    private static partial Regex AssigneeToken();

    [GeneratedRegex(@"(?<!\p{L})(?:(?<dayAfterTomorrow>übermorgen|day\s+after\s+tomorrow)|(?<tomorrow>morgen|tomorrow)|(?<today>heute|today))(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeDateToken();

    [GeneratedRegex(@"(?<!\p{L})(?:nächsten?|next)\s+(?<weekday>montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|monday|tuesday|wednesday|thursday|friday|saturday|sunday)(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NextWeekdayToken();

    [GeneratedRegex(@"(?<!\d)(?<date>\d{4}-\d{2}-\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateToken();

    [GeneratedRegex(@"(?<!\d)(?<date>\d{1,2}[./]\d{1,2}(?:[./](?:\d{2}|\d{4}))?\.?)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDateToken();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed record SmartTaskInputResult(
    string Title,
    DateTime? DueDate,
    bool IsImportant,
    string? AssigneeUserId,
    IReadOnlyList<Guid> LabelIds)
{
    public bool HasMetadata => DueDate.HasValue || IsImportant || AssigneeUserId is not null || LabelIds.Count > 0;
}
