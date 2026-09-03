using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using Microsoft.AspNetCore.Components;

namespace Klassenbibliothek.Services;

/// <summary>
/// Defines the persisted rich-text envelope and is the single trust boundary for
/// user-authored HTML. Unmarked values are always treated as legacy plain text.
/// </summary>
public static class RichTextContent
{
    public const string StoragePrefix = "<!--todosuite-rich-text:v1-->";
    private const string SearchTextPrefix = "<!--todosuite-search-text:";

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    public static bool IsRichText(string? value)
        => value?.StartsWith(StoragePrefix, StringComparison.Ordinal) == true;

    public static string? NormalizeForStorage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Unmarkierte Bestandswerte bleiben reiner Text. Sie werden niemals nachträglich als
        // HTML interpretiert, auch wenn sie zufällig Tags enthalten.
        if (!IsRichText(value))
            return value;

        var html = SanitizeHtml(value[StoragePrefix.Length..]);
        return BuildStorageValue(html);
    }

    public static string? FromEditorHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var sanitized = SanitizeHtml(html);
        return BuildStorageValue(sanitized);
    }

    public static string ToEditorHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Auch bereits gespeicherter Rich Text wird an jeder Ausgabegrenze erneut bereinigt.
        // Damit bleiben Altbestände nach später verschärften Sanitizer-Regeln sicher.
        if (IsRichText(value))
            return SanitizeHtml(value[StoragePrefix.Length..]);

        var encoded = WebUtility.HtmlEncode(value)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return $"<p>{encoded.Replace("\n", "<br>", StringComparison.Ordinal)}</p>";
    }

    public static MarkupString ToMarkupString(string? value)
        => IsRichText(value)
            ? new MarkupString(SanitizeHtml(value![StoragePrefix.Length..]))
            : LinkifiedText.ToMarkupString(value);

    public static string ToSafeHtml(string? value)
    {
        if (IsRichText(value))
            return SanitizeHtml(value![StoragePrefix.Length..]);

        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return WebUtility.HtmlEncode(value)
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Replace("\r", "<br>", StringComparison.Ordinal);
    }

    public static string ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (!IsRichText(value))
            return value;

        var searchTextStart = value.IndexOf(SearchTextPrefix, StoragePrefix.Length, StringComparison.Ordinal);
        if (searchTextStart >= StoragePrefix.Length && value.EndsWith("-->", StringComparison.Ordinal))
        {
            var contentStart = searchTextStart + SearchTextPrefix.Length;
            return value[contentStart..^3];
        }

        return ToPlainTextFromHtml(SanitizeHtml(value[StoragePrefix.Length..]));
    }

    public static string SanitizeHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var sanitized = Sanitizer.Sanitize(html);
        var parser = new HtmlParser();
        var document = parser.ParseDocument($"<body>{sanitized}</body>");
        if (document.Body is not null)
        {
            foreach (var link in document.Body.QuerySelectorAll("a"))
            {
                // Externe Links öffnen getrennt; noopener verhindert den Zugriff der Zielseite
                // auf das ursprüngliche Fenster, noreferrer reduziert Referrer-Leaks.
                link.SetAttribute("target", "_blank");
                link.SetAttribute("rel", "noopener noreferrer");
            }
        }

        return document.Body?.InnerHtml ?? string.Empty;
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        // Positivliste statt Blockliste: unbekannte Tags, Attribute und URL-Schemata sind
        // standardmäßig verboten und müssen bewusst ergänzt werden.
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith([
            "p", "br", "strong", "em", "s", "code", "pre", "blockquote",
            "ul", "ol", "li", "h1", "h2", "h3", "hr", "a"
        ]);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(["href"]);
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.UnionWith(["http", "https", "mailto"]);
        sanitizer.KeepChildNodes = true;
        return sanitizer;
    }

    private static string ToPlainTextFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var document = new HtmlParser().ParseDocument($"<body>{html}</body>");
        if (document.Body is null)
            return string.Empty;

        var output = new StringBuilder();
        AppendText(document.Body, output);
        return NormalizeWhitespace(output.ToString());
    }

    private static string? BuildStorageValue(string sanitizedHtml)
    {
        var plainText = ToPlainTextFromHtml(sanitizedHtml);
        if (string.IsNullOrWhiteSpace(plainText))
            return null;

        // Keeping a sanitized plain-text copy in an HTML comment preserves efficient,
        // database-side phrase search even when formatting tags split adjacent words.
        // The comment is removed again at every render/edit/sanitize boundary.
        var searchable = plainText
            .Replace("--", "—", StringComparison.Ordinal)
            .Replace("\0", string.Empty, StringComparison.Ordinal);
        return $"{StoragePrefix}{sanitizedHtml}{SearchTextPrefix}{searchable}-->";
    }

    private static void AppendText(INode node, StringBuilder output)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
            {
                output.Append(text.Data);
                continue;
            }

            if (child is not IElement element)
                continue;

            var isBlock = element.LocalName is "p" or "pre" or "blockquote" or "li"
                or "h1" or "h2" or "h3" or "ul" or "ol" or "hr";
            if (element.LocalName == "br")
                output.AppendLine();
            else
                AppendText(element, output);

            if (isBlock && output.Length > 0 && output[^1] != '\n')
                output.AppendLine();
        }
    }

    private static string NormalizeWhitespace(string value)
    {
        var lines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToArray();
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
