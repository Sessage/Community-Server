using Klassenbibliothek.Localization;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Klassenbibliothek.Services;

public sealed record ExcelImportRow(int Number, string[] Cells);
public sealed record ExcelImportSheet(string Name, string[] Headers, IReadOnlyList<ExcelImportRow> Rows)
{
    public int HeaderRowNumber { get; init; } = 1;
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ExcelImportRow> SourceRows { get; init; } = [];

    public ExcelImportSheet WithHeaderRow(int number)
    {
        if (number is < 1 or > ExcelImportDocument.MaxExcelRowNumber)
            throw new ArgumentException(SharedText.Get("ExcelImport_HeaderRange"));
        var header = SourceRows.FirstOrDefault(r => r.Number == number)
            ?? throw new ArgumentException(SharedText.Get("ExcelImport_EmptyHeader", number));
        var data = SourceRows.Where(r => r.Number > number).ToArray();
        if (data.Length == 0) throw new ArgumentException(SharedText.Get("ExcelImport_NoData"));
        if (data.Length > ExcelImportDocument.MaxRows)
            throw new ArgumentException(SharedText.Get("ExcelImport_RowLimit"));
        var width = data.Append(header).Max(r => Array.FindLastIndex(r.Cells, v => !string.IsNullOrWhiteSpace(v))) + 1;
        return this with
        {
            HeaderRowNumber = number,
            Headers = header.Cells.Take(width).Select((v, i) => string.IsNullOrWhiteSpace(v) ? SharedText.Get("ExcelImport_Column", i + 1) : v).ToArray(),
            Rows = data.Select(r => r with { Cells = r.Cells.Take(width).ToArray() }).ToArray()
        };
    }
}
public sealed record ExcelImportDocument(bool Date1904, IReadOnlyList<ExcelImportSheet> Sheets)
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxRows = 1000;
    public const int MaxColumns = 128;
    public const int MaxCellLength = 10000;
    public const int MaxExcelRowNumber = 1048576;
    public const int MaxSourceRows = 10000;
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // Read only bounded XML data. Never instantiate Excel, evaluate formulas, extract files,
    // resolve external relationships, or use uploaded names as filesystem paths.
    public static ExcelImportDocument Read(Stream stream)
    {
        try { return ReadCore(stream); }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException or OverflowException or ArgumentException or KeyNotFoundException)
        {
            throw new InvalidDataException(ex is InvalidDataException ? ex.Message : SharedText.Get("ExcelImport_InvalidWorkbook"), ex);
        }
    }

    private static ExcelImportDocument ReadCore(Stream stream)
    {
        if (!stream.CanSeek || stream.Length > MaxFileBytes)
            throw new InvalidDataException(SharedText.Get("ExcelImport_FileTooLarge"));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 512 || archive.Entries.Sum(e => e.Length) > 30 * 1024 * 1024
            || archive.Entries.Any(e => e.Length > 12 * 1024 * 1024)
            || archive.Entries.GroupBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException(SharedText.Get("ExcelImport_ArchiveLimit"));
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Contains("vba", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.Contains("externalLinks", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.StartsWith("xl/embeddings/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(SharedText.Get("ExcelImport_UnsafeWorkbook"));
            if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
                && ReadXml(entry).Descendants().Any(x => string.Equals((string?)x.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(SharedText.Get("ExcelImport_ExternalLinks"));
        }
        XDocument Part(string name) => ReadXml(archive.GetEntry(name)
            ?? throw new InvalidDataException(SharedText.Get("ExcelImport_MissingPart")));
        var workbook = Part("xl/workbook.xml");
        var date1904 = (string?)workbook.Root?.Element(Main + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var relations = (Part("xl/_rels/workbook.xml.rels").Root?.Elements() ?? [])
            .ToDictionary(x => (string?)x.Attribute("Id") ?? throw new InvalidDataException(SharedText.Get("ExcelImport_MissingRelationshipId")),
                x => (string?)x.Attribute("Target") ?? throw new InvalidDataException(SharedText.Get("ExcelImport_MissingRelationshipTarget")));
        var strings = archive.GetEntry("xl/sharedStrings.xml") is { } stringsEntry
            ? ReadXml(stringsEntry).Root!.Elements(Main + "si").Select(Text).ToArray() : [];
        if (strings.Length > 20000) throw new InvalidDataException(SharedText.Get("ExcelImport_StringLimit"));
        var sheets = new List<ExcelImportSheet>();
        var sheetElements = workbook.Root?.Element(Main + "sheets")?.Elements(Main + "sheet").ToArray()
            ?? throw new InvalidDataException(SharedText.Get("ExcelImport_MissingSheetDefinition"));
        if (sheetElements.Length > 10) throw new InvalidDataException(SharedText.Get("ExcelImport_SheetLimit"));
        foreach (var sheet in sheetElements)
        {
            if (sheets.Count >= 10) throw new InvalidDataException(SharedText.Get("ExcelImport_SheetLimit"));
            var target = relations[(string?)sheet.Attribute(Relationships + "id") ?? throw new InvalidDataException(SharedText.Get("ExcelImport_MissingSheetId"))];
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (!path.StartsWith("xl/worksheets/", StringComparison.Ordinal) || path.Contains("..") || path.Contains('\\'))
                throw new InvalidDataException(SharedText.Get("ExcelImport_InvalidSheetReference"));
            var xml = Part(path);
            var rows = new List<ExcelImportRow>();
            var previousRowNumber = 0;
            foreach (var row in xml.Root?.Element(Main + "sheetData")?.Elements(Main + "row")
                ?? throw new InvalidDataException(SharedText.Get("ExcelImport_MissingSheetData")))
            {
                var rowNumber = int.Parse((string?)row.Attribute("r") ?? "0", CultureInfo.InvariantCulture);
                if (rowNumber <= previousRowNumber || rowNumber > MaxExcelRowNumber)
                    throw new InvalidDataException(SharedText.Get("ExcelImport_InvalidRowNumber"));
                previousRowNumber = rowNumber;
                var cells = new string[MaxColumns];
                Array.Fill(cells, "");
                var used = new HashSet<int>();
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? throw new InvalidDataException(SharedText.Get("ExcelImport_MissingCellReference"));
                    var index = 0;
                    foreach (var ch in reference.TakeWhile(char.IsAsciiLetterUpper)) index = checked(index * 26 + ch - 'A' + 1);
                    if (index is < 1 or > MaxColumns || !used.Add(index))
                        throw new InvalidDataException(SharedText.Get("ExcelImport_ColumnLimit"));
                    var cachedValue = cell.Element(Main + "v");
                    if (cell.Element(Main + "f") is not null && cachedValue is null)
                        throw new InvalidDataException(SharedText.Get("ExcelImport_MissingFormulaResult", (string?)sheet.Attribute("name") ?? "", reference));
                    // Formula text is deliberately ignored. Only Excel's cached result enters
                    // the same type, length and task-field validation as ordinary cell values.
                    var raw = (string?)cachedValue ?? "";
                    var value = (string?)cell.Attribute("t") switch
                    {
                        "inlineStr" => Text(cell.Element(Main + "is")),
                        "s" => int.TryParse(raw, out var id) && id >= 0 && id < strings.Length ? strings[id] : throw new InvalidDataException(SharedText.Get("ExcelImport_InvalidStringReference")),
                        "b" => raw == "1" ? "true" : "false",
                        "e" => throw new InvalidDataException(SharedText.Get("ExcelImport_ExcelError")),
                        _ => raw
                    };
                    if (value.Length > MaxCellLength || value.Any(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t'))
                        throw new InvalidDataException(SharedText.Get("ExcelImport_InvalidCell"));
                    cells[index - 1] = value.Trim();
                }
                if (cells.All(string.IsNullOrWhiteSpace)) continue;
                rows.Add(new ExcelImportRow(rowNumber, cells));
                if (rows.Count > MaxSourceRows) throw new InvalidDataException(SharedText.Get("ExcelImport_SourceRowLimit"));
            }
            if (rows.Count < 2) continue;
            var width = rows.Max(r => Array.FindLastIndex(r.Cells, v => !string.IsNullOrWhiteSpace(v))) + 1;
            sheets.Add(new ExcelImportSheet((string?)sheet.Attribute("name") ?? SharedText.Get("ExcelImport_Sheet"),
                rows[0].Cells.Take(width).Select((v, i) => string.IsNullOrWhiteSpace(v) ? SharedText.Get("ExcelImport_Column", i + 1) : v).ToArray(),
                rows.Skip(1).Select(r => r with { Cells = r.Cells.Take(width).ToArray() }).ToArray())
                { HeaderRowNumber = rows[0].Number, SourceRows = rows });
        }
        if (sheets.Count == 0) throw new InvalidDataException(SharedText.Get("ExcelImport_NoSheets"));
        return new ExcelImportDocument(date1904, sheets);
    }

    private static string Text(XElement? element) => string.Concat(element?.Descendants(Main + "t").Select(t => t.Value) ?? []);
    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 12 * 1024 * 1024
        });
        return XDocument.Load(reader);
    }
}
