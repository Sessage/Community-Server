using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace TodoSuite.Server.Services;

public sealed record ExcelImportRow(int Number, string[] Cells);
public sealed record ExcelImportSheet(string Name, string[] Headers, IReadOnlyList<ExcelImportRow> Rows)
{
    public int HeaderRowNumber { get; init; } = 1;
    public IReadOnlyList<ExcelImportRow> SourceRows { get; init; } = [];

    public ExcelImportSheet WithHeaderRow(int number)
    {
        if (number is < 1 or > ExcelImportDocument.MaxExcelRowNumber)
            throw new ArgumentException("Die Überschriftenzeile muss zwischen 1 und 1048576 liegen.");
        var header = SourceRows.FirstOrDefault(r => r.Number == number)
            ?? throw new ArgumentException($"Zeile {number} ist leer oder nicht vorhanden. Bitte eine befüllte Überschriftenzeile wählen.");
        var data = SourceRows.Where(r => r.Number > number).ToArray();
        if (data.Length == 0) throw new ArgumentException("Unterhalb der Überschriftenzeile wurden keine Aufgaben gefunden.");
        if (data.Length > ExcelImportDocument.MaxRows)
            throw new ArgumentException("Maximal 1000 Datenzeilen pro Arbeitsblatt sind erlaubt.");
        var width = data.Append(header).Max(r => Array.FindLastIndex(r.Cells, v => !string.IsNullOrWhiteSpace(v))) + 1;
        return this with
        {
            HeaderRowNumber = number,
            Headers = header.Cells.Take(width).Select((v, i) => string.IsNullOrWhiteSpace(v) ? $"Spalte {i + 1}" : v).ToArray(),
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
            throw new InvalidDataException("Die Datei ist keine unterstützte, gültige XLSX-Datei. " + ex.Message, ex);
        }
    }

    private static ExcelImportDocument ReadCore(Stream stream)
    {
        if (!stream.CanSeek || stream.Length > MaxFileBytes)
            throw new InvalidDataException("Maximal 10 MB sind erlaubt.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 512 || archive.Entries.Sum(e => e.Length) > 30 * 1024 * 1024
            || archive.Entries.Any(e => e.Length > 12 * 1024 * 1024)
            || archive.Entries.GroupBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("Die entpackte Arbeitsmappe ist zu groß oder mehrdeutig.");
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Contains("vba", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.Contains("externalLinks", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.StartsWith("xl/embeddings/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Makros, eingebettete Objekte und externe Verknüpfungen sind nicht erlaubt.");
            if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
                && ReadXml(entry).Descendants().Any(x => string.Equals((string?)x.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Externe Verknüpfungen sind nicht erlaubt.");
        }
        XDocument Part(string name) => ReadXml(archive.GetEntry(name)
            ?? throw new InvalidDataException("Ein erforderlicher Bestandteil der Arbeitsmappe fehlt."));
        var workbook = Part("xl/workbook.xml");
        var date1904 = (string?)workbook.Root?.Element(Main + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var relations = (Part("xl/_rels/workbook.xml.rels").Root?.Elements() ?? [])
            .ToDictionary(x => (string?)x.Attribute("Id") ?? throw new InvalidDataException("Verknüpfungs-ID fehlt."),
                x => (string?)x.Attribute("Target") ?? throw new InvalidDataException("Verknüpfungsziel fehlt."));
        var strings = archive.GetEntry("xl/sharedStrings.xml") is { } stringsEntry
            ? ReadXml(stringsEntry).Root!.Elements(Main + "si").Select(Text).ToArray() : [];
        if (strings.Length > 20000) throw new InvalidDataException("Zu viele gemeinsame Zeichenfolgen.");
        var sheets = new List<ExcelImportSheet>();
        var sheetElements = workbook.Root?.Element(Main + "sheets")?.Elements(Main + "sheet").ToArray()
            ?? throw new InvalidDataException("Die Arbeitsblattdefinition fehlt.");
        if (sheetElements.Length > 10) throw new InvalidDataException("Maximal 10 Arbeitsblätter sind erlaubt.");
        foreach (var sheet in sheetElements)
        {
            if (sheets.Count >= 10) throw new InvalidDataException("Maximal 10 Arbeitsblätter sind erlaubt.");
            var target = relations[(string?)sheet.Attribute(Relationships + "id") ?? throw new InvalidDataException("Arbeitsblatt-ID fehlt.")];
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (!path.StartsWith("xl/worksheets/", StringComparison.Ordinal) || path.Contains("..") || path.Contains('\\'))
                throw new InvalidDataException("Ungültiger Arbeitsblattverweis.");
            var xml = Part(path);
            var rows = new List<ExcelImportRow>();
            var previousRowNumber = 0;
            foreach (var row in xml.Root?.Element(Main + "sheetData")?.Elements(Main + "row")
                ?? throw new InvalidDataException("Arbeitsblattdaten fehlen."))
            {
                var rowNumber = int.Parse((string?)row.Attribute("r") ?? "0", CultureInfo.InvariantCulture);
                if (rowNumber <= previousRowNumber || rowNumber > MaxExcelRowNumber)
                    throw new InvalidDataException("Ungültige oder doppelte Excel-Zeilennummer.");
                previousRowNumber = rowNumber;
                var cells = new string[MaxColumns];
                Array.Fill(cells, "");
                var used = new HashSet<int>();
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? throw new InvalidDataException("Zelladresse fehlt.");
                    var index = 0;
                    foreach (var ch in reference.TakeWhile(char.IsAsciiLetterUpper)) index = checked(index * 26 + ch - 'A' + 1);
                    if (index is < 1 or > MaxColumns || !used.Add(index))
                        throw new InvalidDataException("Maximal 128 eindeutige Spalten sind erlaubt.");
                    var cachedValue = cell.Element(Main + "v");
                    if (cell.Element(Main + "f") is not null && cachedValue is null)
                        throw new InvalidDataException($"Arbeitsblatt „{(string?)sheet.Attribute("name")}“, Zelle {reference}: Für die Formel fehlt das gespeicherte Ergebnis. Bitte die Datei in Excel neu berechnen und speichern.");
                    // Formula text is deliberately ignored. Only Excel's cached result enters
                    // the same type, length and task-field validation as ordinary cell values.
                    var raw = (string?)cachedValue ?? "";
                    var value = (string?)cell.Attribute("t") switch
                    {
                        "inlineStr" => Text(cell.Element(Main + "is")),
                        "s" => int.TryParse(raw, out var id) && id >= 0 && id < strings.Length ? strings[id] : throw new InvalidDataException("Ungültiger Textverweis."),
                        "b" => raw == "1" ? "true" : "false",
                        "e" => throw new InvalidDataException("Das Arbeitsblatt enthält Excel-Fehlerwerte."),
                        _ => raw
                    };
                    if (value.Length > MaxCellLength || value.Any(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t'))
                        throw new InvalidDataException("Eine Zelle enthält zu viel Text oder unzulässige Steuerzeichen.");
                    cells[index - 1] = value.Trim();
                }
                if (cells.All(string.IsNullOrWhiteSpace)) continue;
                rows.Add(new ExcelImportRow(rowNumber, cells));
                if (rows.Count > MaxSourceRows) throw new InvalidDataException("Maximal 10000 befüllte Zeilen einschließlich Vorspann sind erlaubt.");
            }
            if (rows.Count < 2) continue;
            var width = rows.Max(r => Array.FindLastIndex(r.Cells, v => !string.IsNullOrWhiteSpace(v))) + 1;
            sheets.Add(new ExcelImportSheet((string?)sheet.Attribute("name") ?? "Arbeitsblatt",
                rows[0].Cells.Take(width).Select((v, i) => string.IsNullOrWhiteSpace(v) ? $"Spalte {i + 1}" : v).ToArray(),
                rows.Skip(1).Select(r => r with { Cells = r.Cells.Take(width).ToArray() }).ToArray())
                { HeaderRowNumber = rows[0].Number, SourceRows = rows });
        }
        if (sheets.Count == 0) throw new InvalidDataException("Kein Arbeitsblatt mit Überschriften und Daten gefunden.");
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
