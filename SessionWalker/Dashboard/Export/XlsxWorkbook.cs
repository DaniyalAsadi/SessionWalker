using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace SessionWalker.Dashboard.Export;

/// <summary>
/// Minimal, dependency-free XLSX (Office Open XML) writer. Produces a valid
/// .xlsx package (workbook + styles + worksheets) with the formatting a
/// developer to-do workbook needs: styled header row, frozen header pane,
/// auto filter, per-column widths, date number formats and conditional
/// formatting rules.
///
/// Deliberately self-contained (only System.IO.Compression / System.Xml from
/// the BCL) so the export works on machines without extra NuGet packages, and
/// the file can be opened by Excel, LibreOffice and Google Sheets.
/// </summary>
public sealed class XlsxWorkbook : IDisposable
{
    private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const int DateNumberFormatId = 164;
    private const int DateStyleId = 2;
    private const int WrapStyleId = 3;
    private const int DateWrapStyleId = 4;

    // Differential (conditional-formatting) style indexes.
    public const int DxfHigh = 0;
    public const int DxfMedium = 1;
    public const int DxfLow = 2;
    public const int DxfProblem = 3;

    /// <summary>Cell style for long text columns (wrapped, top-aligned).</summary>
    public const int StyleWrapText = 3;

    private static readonly List<XlsxSheetData> _sheets = new List<XlsxSheetData>();
    private bool _disposed;

    public XlsxSheet AddSheet(string name)
    {
        var sheet = new XlsxSheet(_sheets.Count + 1, name);
        _sheets.Add(sheet.Data);
        return sheet;
    }

    public void Save(string path, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(XlsxWorkbook));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        WritePart(archive, "[Content_Types].xml", writer => WriteContentTypes(writer));
        WritePart(archive, "_rels/.rels", writer => WriteRootRelationships(writer));
        WritePart(archive, "xl/workbook.xml", writer => WriteWorkbook(writer));
        WritePart(archive, "xl/_rels/workbook.xml.rels", writer => WriteWorkbookRelationships(writer));
        WritePart(archive, "xl/styles.xml", writer => WriteStyles(writer));

        for (var i = 0; i < _sheets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheetIndex = i + 1;
            WritePart(archive, $"xl/worksheets/sheet{sheetIndex}.xml", writer =>
                WriteWorksheet(writer, _sheets[i], cancellationToken));
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    // ------------------------------------------------------------------
    // Package parts
    // ------------------------------------------------------------------

    private static void WritePart(ZipArchive archive, string entryName, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = CreateXmlWriter(entryStream);
        write(writer);
    }

    private static XmlWriter CreateXmlWriter(Stream stream) =>
        XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = false,
            OmitXmlDeclaration = false
        });

    private static void WriteContentTypes(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");

        writer.WriteStartElement("Default");
        writer.WriteAttributeString("Extension", "rels");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
        writer.WriteEndElement();

        writer.WriteStartElement("Default");
        writer.WriteAttributeString("Extension", "xml");
        writer.WriteAttributeString("ContentType", "application/xml");
        writer.WriteEndElement();

        writer.WriteStartElement("Override");
        writer.WriteAttributeString("PartName", "/xl/workbook.xml");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        writer.WriteEndElement();

        writer.WriteStartElement("Override");
        writer.WriteAttributeString("PartName", "/xl/styles.xml");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        writer.WriteEndElement();

        for (var i = 1; i <= _sheets.Count; i++)
        {
            writer.WriteStartElement("Override");
            writer.WriteAttributeString("PartName", $"/xl/worksheets/sheet{i}.xml");
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRootRelationships(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", "rId1");
        writer.WriteAttributeString("Type", $"{RelationshipsNamespace}/officeDocument");
        writer.WriteAttributeString("Target", "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbook(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("workbook", MainNamespace);

        writer.WriteStartElement("fileVersion");
        writer.WriteAttributeString("appName", "xl");
        writer.WriteAttributeString("lastEdited", "7");
        writer.WriteAttributeString("lowestEdited", "7");
        writer.WriteAttributeString("rupBuild", "0");
        writer.WriteEndElement();

        writer.WriteStartElement("workbookPr");
        writer.WriteAttributeString("defaultThemeVersion", "124226");
        writer.WriteEndElement();

        writer.WriteStartElement("bookViews");
        writer.WriteStartElement("workbookView");
        writer.WriteAttributeString("xWindow", "0");
        writer.WriteAttributeString("yWindow", "0");
        writer.WriteAttributeString("windowWidth", "24000");
        writer.WriteAttributeString("windowHeight", "16000");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("sheets");
        foreach (var sheet in _sheets)
        {
            writer.WriteStartElement("sheet");
            writer.WriteAttributeString("name", sheet.Name);
            writer.WriteAttributeString("sheetId", sheet.Index.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("r", "id", RelationshipsNamespace, $"rId{sheet.Index}");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("calcPr");
        writer.WriteAttributeString("calcId", "191029");
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbookRelationships(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);

        foreach (var sheet in _sheets)
        {
            writer.WriteStartElement("Relationship");
            writer.WriteAttributeString("Id", $"rId{sheet.Index}");
            writer.WriteAttributeString("Type", $"{RelationshipsNamespace}/worksheet");
            writer.WriteAttributeString("Target", $"worksheets/sheet{sheet.Index}.xml");
            writer.WriteEndElement();
        }

        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", $"rId{_sheets.Count + 1}");
        writer.WriteAttributeString("Type", $"{RelationshipsNamespace}/styles");
        writer.WriteAttributeString("Target", "styles.xml");
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteStyles(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("styleSheet", MainNamespace);

        // Number formats (custom dates).
        writer.WriteStartElement("numFmts");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("numFmt");
        writer.WriteAttributeString("numFmtId", DateNumberFormatId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("formatCode", "yyyy-mm-dd hh:mm");
        writer.WriteEndElement();
        writer.WriteEndElement();

        // Fonts: 0 default, 1 header bold white, 2-4 conditional-formatting colors.
        writer.WriteStartElement("fonts");
        writer.WriteAttributeString("count", "5");
        WriteFontEntry(writer, "Calibri", bold: false, color: "FF000000");
        WriteFontEntry(writer, "Calibri", bold: true, color: "FFFFFFFF");
        WriteFontEntry(writer, "Calibri", bold: true, color: "FF9C0006");
        WriteFontEntry(writer, "Calibri", bold: true, color: "FF9C6500");
        WriteFontEntry(writer, "Calibri", bold: true, color: "FF006100");
        writer.WriteEndElement();

        // Fills: 0 none, 1 gray125 (schema requirement), 2 header, 3-5 CF colors.
        writer.WriteStartElement("fills");
        writer.WriteAttributeString("count", "6");
        WriteFillEntry(writer, "none", null);
        WriteFillEntry(writer, "gray125", null);
        WriteFillEntry(writer, "solid", "FF1F4E78");
        WriteFillEntry(writer, "solid", "FFFFC7CE");
        WriteFillEntry(writer, "solid", "FFFFEB9C");
        WriteFillEntry(writer, "solid", "FFC6EFCE");
        writer.WriteEndElement();

        // Borders: 0 none, 1 thin light.
        writer.WriteStartElement("borders");
        writer.WriteAttributeString("count", "2");
        WriteBorderEntry(writer, include: false);
        WriteBorderEntry(writer, include: true);
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyleXfs");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("xf");
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", "0");
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellXfs");
        writer.WriteAttributeString("count", "5");

        // 0: default data cell.
        WriteDataCellXf(writer, numFmtId: 0, fontId: 0, fillId: 0, borderId: 0, wrap: false, center: false);

        // 1: header.
        writer.WriteStartElement("xf");
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", "1");
        writer.WriteAttributeString("fillId", "2");
        writer.WriteAttributeString("borderId", "1");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("applyFont", "1");
        writer.WriteAttributeString("applyFill", "1");
        writer.WriteAttributeString("applyBorder", "1");
        writer.WriteAttributeString("applyAlignment", "1");
        writer.WriteStartElement("alignment");
        writer.WriteAttributeString("horizontal", "center");
        writer.WriteAttributeString("vertical", "center");
        writer.WriteAttributeString("wrapText", "1");
        writer.WriteEndElement();
        writer.WriteEndElement();

        // 2: date.
        WriteDataCellXf(writer, DateNumberFormatId, 0, 0, 0, wrap: false, center: false);

        // 3: wrapped text.
        WriteDataCellXf(writer, 0, 0, 0, 0, wrap: true, center: false);

        // 4: date + wrapped text.
        WriteDataCellXf(writer, DateNumberFormatId, 0, 0, 0, wrap: true, center: false);

        writer.WriteEndElement(); // cellXfs

        writer.WriteStartElement("cellStyles");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("cellStyle");
        writer.WriteAttributeString("name", "Normal");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("builtinId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        // Differential styles used by conditional formatting.
        writer.WriteStartElement("dxfs");
        writer.WriteAttributeString("count", "4");
        WriteDxf(writer, "FF9C0006", "FFFFC7CE"); // high / problem
        WriteDxf(writer, "FF9C6500", "FFFFEB9C"); // medium / in progress
        WriteDxf(writer, "FF006100", "FFC6EFCE"); // low / completed
        WriteDxf(writer, "FF9C0006", "FFFFC7CE"); // numeric problem (>0)
        writer.WriteEndElement();

        writer.WriteEndElement(); // styleSheet
        writer.WriteEndDocument();
    }

    private static void WriteFontEntry(XmlWriter writer, string name, bool bold, string color)
    {
        writer.WriteStartElement("font");
        writer.WriteStartElement("sz");
        writer.WriteAttributeString("val", "11");
        writer.WriteEndElement();

        writer.WriteStartElement("name");
        writer.WriteAttributeString("val", name);
        writer.WriteEndElement();

        if (bold)
        {
            writer.WriteStartElement("b");
            writer.WriteEndElement();
        }

        writer.WriteStartElement("color");
        writer.WriteAttributeString("rgb", color);
        writer.WriteEndElement();

        writer.WriteStartElement("family");
        writer.WriteAttributeString("val", "2");
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void WriteFillEntry(XmlWriter writer, string patternType, string? rgb)
    {
        writer.WriteStartElement("fill");
        writer.WriteStartElement("patternFill");
        writer.WriteAttributeString("patternType", patternType);
        if (rgb is not null)
        {
            writer.WriteStartElement("fgColor");
            writer.WriteAttributeString("rgb", rgb);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteBorderEntry(XmlWriter writer, bool include)
    {
        writer.WriteStartElement("border");
        WriteBorderSide(writer, "left", include);
        WriteBorderSide(writer, "right", include);
        WriteBorderSide(writer, "top", include);
        WriteBorderSide(writer, "bottom", include);
        writer.WriteStartElement("diagonal");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteBorderSide(XmlWriter writer, string side, bool styled)
    {
        writer.WriteStartElement(side);
        if (styled)
        {
            writer.WriteAttributeString("style", "thin");
            writer.WriteStartElement("color");
            writer.WriteAttributeString("rgb", "FFD9D9D9");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteDataCellXf(XmlWriter writer, int numFmtId, int fontId, int fillId, int borderId, bool wrap, bool center)
    {
        writer.WriteStartElement("xf");
        writer.WriteAttributeString("numFmtId", numFmtId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fontId", fontId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fillId", fillId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("borderId", borderId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("applyAlignment", "1");
        if (numFmtId != 0)
        {
            writer.WriteAttributeString("applyNumberFormat", "1");
        }
        writer.WriteStartElement("alignment");
        writer.WriteAttributeString("vertical", "top");
        if (wrap)
        {
            writer.WriteAttributeString("wrapText", "1");
        }
        if (center)
        {
            writer.WriteAttributeString("horizontal", "center");
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteDxf(XmlWriter writer, string fontRgb, string fillRgb)
    {
        writer.WriteStartElement("dxf");
        writer.WriteStartElement("font");
        writer.WriteStartElement("b");
        writer.WriteEndElement();
        writer.WriteStartElement("color");
        writer.WriteAttributeString("rgb", fontRgb);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("fill");
        writer.WriteStartElement("patternFill");
        writer.WriteStartElement("bgColor");
        writer.WriteAttributeString("rgb", fillRgb);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void WriteWorksheet(XmlWriter writer, XlsxSheetData sheet, CancellationToken cancellationToken)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", MainNamespace);

        var rowCount = sheet.RowCount;
        var columnCount = sheet.ColumnCount;
        var lastColumn = columnCount > 0 ? ColumnName(columnCount - 1) : "A";
        var lastCell = rowCount > 0 ? $"{lastColumn}{rowCount}" : "A1";

        writer.WriteStartElement("dimension");
        writer.WriteAttributeString("ref", $"A1:{lastCell}");
        writer.WriteEndElement();

        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane");
        writer.WriteAttributeString("ySplit", "1");
        writer.WriteAttributeString("topLeftCell", "A2");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteStartElement("selection");
        writer.WriteAttributeString("pane", "bottomLeft");
        writer.WriteAttributeString("activeCell", "A2");
        writer.WriteAttributeString("sqref", "A2");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("sheetFormatPr");
        writer.WriteAttributeString("defaultRowHeight", "15");
        writer.WriteEndElement();

        var widths = ComputeWidths(sheet);
        writer.WriteStartElement("cols");
        for (var i = 0; i < widths.Length; i++)
        {
            writer.WriteStartElement("col");
            var number = i + 1;
            writer.WriteAttributeString("min", number.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", number.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", widths[i].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("sheetData");
        for (var r = 0; r < rowCount; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteRow(writer, sheet, r + 1);
        }
        writer.WriteEndElement();

        if (rowCount > 0)
        {
            writer.WriteStartElement("autoFilter");
            writer.WriteAttributeString("ref", $"A1:{lastCell}");
            writer.WriteEndElement();
        }

        WriteConditionalFormatting(writer, sheet, rowCount);

        writer.WriteStartElement("pageMargins");
        writer.WriteAttributeString("left", "0.7");
        writer.WriteAttributeString("right", "0.7");
        writer.WriteAttributeString("top", "0.75");
        writer.WriteAttributeString("bottom", "0.75");
        writer.WriteAttributeString("header", "0.3");
        writer.WriteAttributeString("footer", "0.3");
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRow(XmlWriter writer, XlsxSheetData sheet, int rowNumber)
    {
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));

        for (var c = 0; c < sheet.ColumnCount; c++)
        {
            var value = sheet.GetCell(rowNumber - 1, c);
            var columnStyle = sheet.GetColumnStyle(c);
            var isDate = value is DateTime or DateTimeOffset;
            var styleId = rowNumber == 1
                ? 1 // header
                : isDate
                    ? (columnStyle == WrapStyleId ? DateWrapStyleId : DateStyleId)
                    : columnStyle;

            WriteCell(writer, rowNumber, c + 1, value, styleId);
        }

        writer.WriteEndElement();
    }

    private static void WriteCell(XmlWriter writer, int row, int column, object? value, int styleId)
    {
        var cellReference = $"{ColumnName(column - 1)}{row}";
        writer.WriteStartElement("c");
        writer.WriteAttributeString("r", cellReference);

        if (styleId != 0)
        {
            writer.WriteAttributeString("s", styleId.ToString(CultureInfo.InvariantCulture));
        }

        if (value is null)
        {
            writer.WriteEndElement();
            return;
        }

        switch (value)
        {
            case DateTime dateTime:
                writer.WriteStartElement("v");
                writer.WriteString(ToOADate(dateTime));
                writer.WriteEndElement();
                break;

            case DateTimeOffset dateTimeOffset:
                writer.WriteStartElement("v");
                writer.WriteString(ToOADate(dateTimeOffset.ToLocalTime().DateTime));
                writer.WriteEndElement();
                break;

            case bool boolean:
                writer.WriteAttributeString("t", "b");
                writer.WriteStartElement("v");
                writer.WriteString(boolean ? "1" : "0");
                writer.WriteEndElement();
                break;

            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                writer.WriteStartElement("v");
                writer.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture));
                writer.WriteEndElement();
                break;

            default:
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is");
                writer.WriteStartElement("t");
                writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                writer.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture));
                writer.WriteEndElement();
                writer.WriteEndElement();
                break;
        }

        writer.WriteEndElement();
    }

    private static void WriteConditionalFormatting(XmlWriter writer, XlsxSheetData sheet, int rowCount)
    {
        if (rowCount < 2)
        {
            return;
        }

        var priority = 1;
        foreach (var rule in sheet.Rules)
        {
            var startRow = 2;
            var columnLetter = ColumnName(rule.Column);
            writer.WriteStartElement("conditionalFormatting");
            writer.WriteAttributeString("sqref", $"{columnLetter}{startRow}:{columnLetter}{rowCount}");

            writer.WriteStartElement("cfRule");
            writer.WriteAttributeString("type", "cellIs");
            writer.WriteAttributeString("dxfId", rule.StyleIndex.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("priority", priority.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("operator", rule.Operator);
            writer.WriteStartElement("formula");
            writer.WriteString(rule.Formula);
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteEndElement();
            priority++;
        }
    }

    private static string ToOADate(DateTime value) =>
        value.ToOADate().ToString(CultureInfo.InvariantCulture);

    private static int[] ComputeWidths(XlsxSheetData sheet)
    {
        var widths = new int[sheet.ColumnCount];
        for (var c = 0; c < sheet.ColumnCount; c++)
        {
            var maxLength = 0;
            for (var r = 0; r < sheet.RowCount; r++)
            {
                var value = sheet.GetCell(r, c);
                if (value is null)
                {
                    continue;
                }

                var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                var length = text
                    .Replace("\r", string.Empty)
                    .Replace("\n", " ")
                    .TrimEnd()
                    .Length;

                if (length > maxLength)
                {
                    maxLength = length;
                }
            }

            var cap = sheet.GetColumnStyle(c) == WrapStyleId ? 50 : 60;
            var width = Math.Min(cap, Math.Max(10, maxLength + 2));
            widths[c] = width;
        }

        return widths;
    }

    private static string ColumnName(int zeroBasedIndex)
    {
        var name = string.Empty;
        var value = zeroBasedIndex + 1;
        while (value > 0)
        {
            var remainder = (value - 1) % 26;
            name = (char)('A' + remainder) + name;
            value = (value - 1) / 26;
        }

        return name;
    }

    // ------------------------------------------------------------------
    // Internal per-sheet state
    // ------------------------------------------------------------------

    internal sealed class XlsxSheetData
    {
        public XlsxSheetData(int index, string name)
        {
            Index = index;
            Name = name;
        }

        public int Index { get; }
        public string Name { get; }

        public List<object?[]> Rows { get; } = new List<object?[]>();
        public int[]? ColumnStyles { get; set; }
        public List<ConditionalRule> Rules { get; } = new List<ConditionalRule>();

        public int ColumnCount => Rows.Count == 0 ? 0 : Rows[0].Length;
        public int RowCount => Rows.Count;

        public object? GetCell(int row, int column) =>
            row < Rows.Count && column < Rows[row].Length ? Rows[row][column] : null;

        public int GetColumnStyle(int column) =>
            ColumnStyles is not null && column < ColumnStyles.Length ? ColumnStyles[column] : 0;
    }

    internal sealed record ConditionalRule(
        int Column,
        string Operator,
        string Formula,
        int StyleIndex)
    {
        public int Column { get; } = Column;
        public string Operator { get; } = Operator;
        public string Formula { get; } = Formula;
        public int StyleIndex { get; } = StyleIndex;
    }
}

/// <summary>
/// A single worksheet being built by <see cref="XlsxWorkbook"/>.
/// </summary>
public sealed class XlsxSheet
{
    internal XlsxSheet(int index, string name)
    {
        Data = new XlsxWorkbook.XlsxSheetData(index, name);
    }

    internal XlsxWorkbook.XlsxSheetData Data { get; }

    /// <summary>First row is emitted as the styled, frozen header row.</summary>
    public void SetHeader(IReadOnlyList<string> columns)
    {
        Data.Rows.Add(columns.Cast<object?>().ToArray());
    }

    public void AddRow(params object?[] values)
    {
        Data.Rows.Add(values);
    }

    /// <summary>Sets the cell style applied to every cell of a 0-based column (e.g. wrapped text).</summary>
    public void SetColumnStyle(int column, int styleId)
    {
        var requiredLength = column + 1;
        if (Data.ColumnStyles is null)
        {
            Data.ColumnStyles = new int[requiredLength];
        }
        else if (Data.ColumnStyles.Length < requiredLength)
        {
            var resized = new int[requiredLength];
            Array.Copy(Data.ColumnStyles, resized, Data.ColumnStyles.Length);
            Data.ColumnStyles = resized;
        }

        Data.ColumnStyles[column] = styleId;
    }

    public void AddTextRule(int column, string expectedValue, int dxfStyle)
    {
        var escaped = expectedValue.Replace("\"", "\"\"");
        Data.Rules.Add(new XlsxWorkbook.ConditionalRule(column, "equal", $"\"{escaped}\"", dxfStyle));
    }

    public void AddNumberRule(int column, string operation, double value, int dxfStyle)
    {
        Data.Rules.Add(new XlsxWorkbook.ConditionalRule(
            column,
            operation,
            value.ToString(CultureInfo.InvariantCulture),
            dxfStyle));
    }
}
