using System.Globalization;
using Klassenbibliothek.Data;

namespace Klassenbibliothek.Services;

/// <summary>Shared parsing and date calculation for persisted task recurrence rules.</summary>
public static class TaskRecurrenceRules
{
    public const int DefaultCompletionIntervalDays = 14;
    public const int MinimumCompletionIntervalDays = 1;
    public const int MaximumCompletionIntervalDays = 36500;
    private const string CompletionPrefix = "after-completion:";

    public static string EncodeCompletionInterval(int days)
    {
        ValidateCompletionInterval(days);
        return CompletionPrefix + days.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryGetCompletionInterval(string? value, out int days)
    {
        days = 0;
        if (string.IsNullOrWhiteSpace(value)
            || !value.Trim().StartsWith(CompletionPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var number = value.Trim()[CompletionPrefix.Length..];
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out days)
               && days is >= MinimumCompletionIntervalDays and <= MaximumCompletionIntervalDays;
    }

    public static string? NormalizeCustomRule(RecurrencePattern recurrence, string? value)
    {
        if (!Enum.IsDefined(recurrence))
            throw new ArgumentOutOfRangeException(nameof(recurrence), recurrence, "Unbekanntes Wiederholungsmuster.");

        if (recurrence == RecurrencePattern.TageNachAbschluss)
        {
            if (!TryGetCompletionInterval(value, out var days))
                throw new ArgumentException(
                    $"Das Wiederholungsintervall muss zwischen {MinimumCompletionIntervalDays} und {MaximumCompletionIntervalDays} Tagen liegen.",
                    nameof(value));
            return EncodeCompletionInterval(days);
        }

        return recurrence == RecurrencePattern.Benutzerdefiniert && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    public static DateTime? CalculateNextDueDate(
        RecurrencePattern recurrence,
        DateTime? currentDueDate,
        DateTime completedOn,
        string? customRule)
    {
        var completionDate = completedOn.Date;
        if (recurrence == RecurrencePattern.TageNachAbschluss)
        {
            if (!TryGetCompletionInterval(customRule, out var days))
                return null;
            return completionDate.AddDays(days);
        }

        var baseDate = (currentDueDate ?? completionDate).Date;
        return recurrence switch
        {
            RecurrencePattern.Taeglich => baseDate.AddDays(1),
            RecurrencePattern.Woechentlich => baseDate.AddDays(7),
            RecurrencePattern.BestimmteWochentage => baseDate.AddDays(7),
            RecurrencePattern.Monatlich => baseDate.AddMonths(1),
            RecurrencePattern.Jaehrlich => baseDate.AddYears(1),
            _ => null
        };
    }

    private static void ValidateCompletionInterval(int days)
    {
        if (days is < MinimumCompletionIntervalDays or > MaximumCompletionIntervalDays)
            throw new ArgumentOutOfRangeException(nameof(days), days,
                $"Das Wiederholungsintervall muss zwischen {MinimumCompletionIntervalDays} und {MaximumCompletionIntervalDays} Tagen liegen.");
    }
}
