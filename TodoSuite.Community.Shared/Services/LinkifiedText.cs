using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace Klassenbibliothek.Services;

public static partial class LinkifiedText
{
    public static MarkupString ToMarkupString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new MarkupString(string.Empty);

        // Preserve intentional leading/trailing whitespace. Callers use whitespace-pre-line,
        // so trimming here changed the displayed form description or help text.
        var input = text;
        var html = new StringBuilder(input.Length);
        var index = 0;

        foreach (Match match in LinkPattern().Matches(input))
        {
            if (match.Index > index)
                html.Append(WebUtility.HtmlEncode(input[index..match.Index]));

            var trailingText = string.Empty;
            var displayText = match.Value;
            while (displayText.Length > 0 && IsTrailingPunctuation(displayText[^1]))
            {
                trailingText = displayText[^1] + trailingText;
                displayText = displayText[..^1];
            }

            var href = displayText.Contains('@') && !displayText.Contains("://", StringComparison.Ordinal)
                ? $"mailto:{displayText}"
                : displayText.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? $"https://{displayText}"
                    : displayText;

            html.Append("<a class=\"font-medium text-blue-700 underline underline-offset-2 hover:text-blue-800\" href=\"")
                .Append(WebUtility.HtmlEncode(href))
                .Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
                .Append(WebUtility.HtmlEncode(displayText))
                .Append("</a>");
            html.Append(WebUtility.HtmlEncode(trailingText));

            index = match.Index + match.Length;
        }

        if (index < input.Length)
            html.Append(WebUtility.HtmlEncode(input[index..]));

        return new MarkupString(html.ToString());
    }

    [GeneratedRegex(@"(?i)\b(?:https?://[^\s<>()]+|www\.[^\s<>()]+|[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,})")]
    private static partial Regex LinkPattern();

    private static bool IsTrailingPunctuation(char value)
        => value is '.' or ',' or ';' or ':' or '!' or '?';
}
