using System.Globalization;

namespace Klassenbibliothek.Localization;

public static class UserLanguagePreferences
{
    public static readonly IReadOnlyList<string> SupportedCultures =
        ["de", "en", "zh-Hans", "hi", "es", "fr"];

    public static string? Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture) || culture.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;

        var candidate = culture.Trim();
        if (candidate.Equals("zh", StringComparison.OrdinalIgnoreCase))
            return "zh-Hans";

        return SupportedCultures.FirstOrDefault(supported =>
            supported.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryNormalize(string? culture, out string? normalized)
    {
        normalized = Normalize(culture);
        return normalized is not null
               || string.IsNullOrWhiteSpace(culture)
               || culture.Equals("auto", StringComparison.OrdinalIgnoreCase);
    }

    public static CultureInfo Resolve(string? preferredCulture, CultureInfo fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        var normalized = Normalize(preferredCulture);
        if (normalized is not null)
            return CultureInfo.GetCultureInfo(normalized);

        var exact = Normalize(fallback.Name);
        if (exact is not null)
            return CultureInfo.GetCultureInfo(exact);

        var neutral = Normalize(fallback.TwoLetterISOLanguageName);
        return CultureInfo.GetCultureInfo(neutral ?? "de");
    }
}
