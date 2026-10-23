namespace Klassenbibliothek.Services;

/// <summary>Converts wall-clock reminder input using the zone's rules at the target date.</summary>
public static class UserDateTime
{
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            throw new ArgumentException("The selected local time does not exist because the clocks change.");
        // During the repeated hour choose its first occurrence, consistently on every host.
        if (zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static DateTime FromUtc(DateTime utc, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
}
