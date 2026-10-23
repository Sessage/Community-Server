using System.Globalization;

namespace Klassenbibliothek.Services;

/// <summary>Chooses the higher-contrast opaque black/white foreground for an RGB color.</summary>
public static class ColorContrast
{
    public static string TextColor(string background)
    {
        var hex = background.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return "#000000";
        static double Linear(uint channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear((rgb >> 16) & 255) + 0.7152 * Linear((rgb >> 8) & 255) + 0.0722 * Linear(rgb & 255);
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? "#000000" : "#ffffff";
    }
}
