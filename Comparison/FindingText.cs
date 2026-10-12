using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.Comparison;

/// <summary>Reviewer-facing wording for findings, shared by the viewer and the PDF report so both describe a finding the same way.</summary>
public static class FindingText
{
    public const string NotPresent = "Not present";

    /// <summary>What happened, in the reviewer's words, for example "Row missing from Current" or "Bold changed".</summary>
    public static string Operation(DocumentDifference difference) => difference.Category switch
    {
        DifferenceCategory.Review => "Needs review",
        DifferenceCategory.Text when IsSpaceChange(difference) => difference.CurrentValue == "(no corresponding content)" ? "Space missing in Current" : "Spacing changed",
        _ when difference.Property == "Missing row value" => "Row missing from Current",
        _ when difference.Property == "Missing column value" => "Column missing from Current",
        _ when difference.Property == "Paragraph alignment" => "Text alignment changed",
        DifferenceCategory.Formatting when difference.Property is "Subscript" or "Superscript" =>
            difference.Property + (difference.ReferenceValue == "On" ? " removed" : " added"),
        DifferenceCategory.Formatting when difference.Property == "Hidden" =>
            difference.CurrentValue == "On" ? "Text hidden in Current" : "Hidden text shown in Current",
        DifferenceCategory.Formatting => difference.Property + " changed",
        _ when difference.Property.Contains("row value") || difference.Property.Contains("column value") => difference.Property,
        DifferenceCategory.ParagraphAdded => "Inserted paragraph",
        DifferenceCategory.ParagraphRemoved => "Deleted paragraph",
        DifferenceCategory.Table when difference.ReferenceValue == "(no corresponding table)" => "Table added in Current",
        DifferenceCategory.Table when difference.CurrentValue == "(no corresponding table)" => "Table missing from Current",
        DifferenceCategory.Table => "Table changed",
        DifferenceCategory.Image when difference.ReferenceValue == "(no corresponding image)" => "Image added in Current",
        DifferenceCategory.Image when difference.CurrentValue == "(no corresponding image)" => "Image missing from Current",
        DifferenceCategory.Image when difference.IsRecognizedText => "Possible text change in image",
        DifferenceCategory.Image => difference.Property,
        DifferenceCategory.Text when difference.ReferenceValue == "(no corresponding content)" => "Inserted text",
        DifferenceCategory.Text when difference.CurrentValue == "(no corresponding content)" => "Deleted text",
        _ => "Text changed"
    };

    /// <summary>A text finding whose value on either side is only whitespace.</summary>
    public static bool IsSpaceChange(DocumentDifference difference) => difference.Category == DifferenceCategory.Text &&
        (difference.ReferenceValue.Length > 0 && string.IsNullOrWhiteSpace(difference.ReferenceValue) ||
         difference.CurrentValue.Length > 0 && string.IsNullOrWhiteSpace(difference.CurrentValue));

    /// <summary>The engine's marker for content that exists on the other side only.</summary>
    public static bool IsAbsent(string value) =>
        value is "(no corresponding content)" or "(no corresponding table)" or "(no corresponding image)";

    /// <summary>
    /// A value as printed in the report. Absent content reads "Not present", and whitespace is named
    /// ("[space]", "[tab]") because blank text cannot be seen on paper.
    /// </summary>
    public static string PrintableValue(string value)
    {
        if (IsAbsent(value))
            return NotPresent;
        if (value.Length == 0)
            return "(empty)";
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        return string.Concat(value.Select(character => character switch
        {
            ' ' => "[space]",
            '\t' => "[tab]",
            '\n' => "[line break]",
            ' ' => "[non-breaking space]",
            _ => "[whitespace]"
        }));
    }

    /// <summary>
    /// Where a finding is, as a reviewer finds it in Word: the header, footer or note it is in (otherwise its page),
    /// then the paragraph counted the way a reader counts it (paragraphs of that area only, without table cells),
    /// or the table cell with its row label and column header. Word's internal run numbers are never shown.
    /// </summary>
    public static string Location(DocumentModel? document, DocumentLocation? location, bool isImage = false)
    {
        if (location is null)
            return NotPresent;
        var paragraph = document?.Paragraphs.FirstOrDefault(item => item.Index == location.ParagraphIndex);
        var place = paragraph?.Part?.Name ?? location.PageDisplay;
        var cell = location.TableCell ?? paragraph?.TableLocation;
        if (cell is null)
        {
            var number = ParagraphNumber(document!, paragraph) ?? location.ParagraphIndex + 1;
            return isImage ? $"{place}, Image, Paragraph {number}" : $"{place}, Paragraph {number}";
        }
        var table = FindTable(document, cell.TableIndex);
        var labels = string.Join(" · ", new[] { CellText(table?.Rows.ElementAtOrDefault(cell.RowIndex)?.Cells.FirstOrDefault()), ColumnHeader(table, cell) }
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(ShortLabel));
        return $"{place} · Table {cell.TableIndex + 1}, Row {cell.RowIndex + 1}, Column {GridColumnAt(table, cell) + 1}" +
            (labels.Length == 0 ? string.Empty : $" ({labels})");
    }

    /// <summary>The paragraph's number among the paragraphs outside tables in the same area (body, header, footer or note).</summary>
    private static int? ParagraphNumber(DocumentModel document, ParagraphBlock? paragraph) => paragraph is null
        ? null
        : document.Paragraphs.Count(item => !item.IsInTable && item.Part?.Key == paragraph.Part?.Key && item.Index <= paragraph.Index);

    /// <summary>Finds a table anywhere in the document, including inside headers, footers and notes.</summary>
    public static TableDisplayBlock? FindTable(DocumentModel? document, int tableIndex) => document?.DisplayBlocks
        .SelectMany(block => block is PartDisplayBlock part ? part.Blocks : [block])
        .OfType<TableDisplayBlock>()
        .FirstOrDefault(table => table.TableIndex == tableIndex);

    /// <summary>A cell's first grid column; cells built without grid positions use their place in the row.</summary>
    public static int GridColumnOf(DocumentTableCell cell, int indexInRow) => cell.GridColumn >= 0 ? cell.GridColumn : indexInRow;

    /// <summary>The grid column (0-based) of a cell; merged cells to its left count by the columns they cover.</summary>
    public static int GridColumnAt(TableDisplayBlock? table, TableCellLocation cell) =>
        table?.Rows.ElementAtOrDefault(cell.RowIndex)?.Cells.ElementAtOrDefault(cell.CellIndex) is { } target
            ? GridColumnOf(target, cell.CellIndex)
            : cell.CellIndex;

    /// <summary>The header text above a cell: the first-row cell that covers the same grid column.</summary>
    public static string ColumnHeader(TableDisplayBlock? table, TableCellLocation cell)
    {
        var column = GridColumnAt(table, cell);
        var header = table?.Rows.FirstOrDefault()?.Cells
            .Select((candidate, index) => (Cell: candidate, Start: GridColumnOf(candidate, index)))
            .FirstOrDefault(item => item.Start <= column && column < item.Start + item.Cell.ColumnSpan).Cell;
        return CellText(header);
    }

    public static string CellText(DocumentTableCell? cell) => cell is null ? string.Empty : string.Join(" ", cell.Paragraphs.Select(paragraph => paragraph.Text));

    public static string ShortLabel(string text) => text.Length <= 70 ? text : text[..67] + "…";
}
