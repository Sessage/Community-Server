using System.Globalization;
using System.Resources;

namespace Klassenbibliothek.Localization;

public static class SharedText
{
    private static readonly ResourceManager Resources = new(typeof(SharedResource));

    public static string Get(string key, params object[] arguments)
        => string.Format(CultureInfo.CurrentCulture, GetForCulture(key, CultureInfo.CurrentUICulture), arguments);

    public static string GetForCulture(string key, CultureInfo culture)
        => Resources.GetString(key, culture) ?? key;
}
