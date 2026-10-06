namespace DocumentChecker.Engine.Reading;

public sealed record DocumentModel(
    string FileName,
    IReadOnlyList<ParagraphBlock> Paragraphs,
    IReadOnlyList<DocumentImage>? Images = null,
    IReadOnlyList<DocumentDisplayBlock>? Blocks = null,
    IReadOnlyList<DocumentReviewItem>? ReviewNotes = null)
{
    public IReadOnlyList<DocumentImage> ImageItems => Images ?? [];
    public IReadOnlyList<DocumentDisplayBlock> DisplayBlocks => Blocks ?? Paragraphs
        .Select(paragraph => new ParagraphDisplayBlock(paragraph))
        .ToList();
}

public enum DocumentPartKind { Header, Footer, Footnote, Endnote }

/// <summary>
/// A header, footer or note. Content outside any part is the document body (Part is null).
/// Key pairs the same part across documents; Name is shown to the reviewer.
/// </summary>
public sealed record DocumentPart(DocumentPartKind Kind, string Key, string Name);

public sealed record ParagraphBlock(
    int Index,
    IReadOnlyList<TextRun> Runs,
    ParagraphLayout? Layout = null,
    TableCellLocation? TableLocation = null,
    IReadOnlyList<DocumentPagePosition>? PagePositions = null,
    DocumentPart? Part = null)
{
    public string Text => string.Concat(Runs.Select(run => run.Text));
    public bool IsInTable => TableLocation is not null;
    public DocumentPage? PageAt(int characterOffset) => PagePositions?.LastOrDefault(position => position.Start <= characterOffset)?.Page;
}

public enum PageNumberSource { SavedLayout, ExplicitBreaks }

public sealed record DocumentPage(int Number, PageNumberSource Source)
{
    public string Display => Source == PageNumberSource.SavedLayout
        ? $"Page {Number} (saved layout)" : $"Page {Number} (estimated)";
}

public sealed record DocumentPagePosition(int Start, DocumentPage Page);

public abstract record DocumentDisplayBlock;

public sealed record ParagraphDisplayBlock(ParagraphBlock Paragraph) : DocumentDisplayBlock;

public sealed record TableDisplayBlock(IReadOnlyList<DocumentTableRow> Rows, int TableIndex = 0, DocumentPart? Part = null) : DocumentDisplayBlock;

/// <summary>The content of one header, footer or note, shown as its own labelled area.</summary>
public sealed record PartDisplayBlock(DocumentPart Part, IReadOnlyList<DocumentDisplayBlock> Blocks) : DocumentDisplayBlock;

public sealed record DocumentTableRow(IReadOnlyList<DocumentTableCell> Cells);

/// <summary>
/// A table cell. GridColumn is the cell's first column in the table grid (-1 when unknown: use its position
/// in the row). ColumnSpan and RowSpan describe merged cells; a cell merged into the cell above it has
/// MergedFromAbove set and is covered by that cell when the table is drawn.
/// </summary>
public sealed record DocumentTableCell(
    IReadOnlyList<ParagraphBlock> Paragraphs,
    int GridColumn = -1,
    int ColumnSpan = 1,
    int RowSpan = 1,
    bool MergedFromAbove = false);

public sealed record TableCellLocation(int TableIndex, int RowIndex, int CellIndex);

public sealed record ParagraphLayout(
    string? StyleId,
    string? Alignment,
    int? LeftIndentTwips,
    int? FirstLineIndentTwips,
    int? SpaceBeforeTwips,
    int? SpaceAfterTwips,
    bool PageBreakBefore);

public sealed record TextRun(
    int Index,
    string Text,
    RunFormatting Formatting,
    IReadOnlyList<string>? ImageIds = null)
{
    public IReadOnlyList<string> ImageReferences => ImageIds ?? [];
}

public sealed record DocumentImage(
    string Id,
    int ParagraphIndex,
    int RunIndex,
    byte[] Data,
    string ContentType,
    long? WidthEmu,
    long? HeightEmu,
    string? AltText = null,
    string? Caption = null,
    string? Alignment = null,
    string? RelationshipId = null,
    DocumentPage? Page = null);

public sealed record RunFormatting(
    string? FontFamily,
    string? FontSizeHalfPoints,
    bool? Bold,
    bool? Italic,
    string? Underline,
    bool? Strikethrough,
    bool? Subscript,
    bool? Superscript,
    string? FontColor,
    string? Highlight,
    int? CharacterSpacingTwips);

/// <summary>Content the comparison cannot verify. A null paragraph and table means the whole document (headers, footers, notes).</summary>
public sealed record DocumentReviewItem(string Message, int? ParagraphIndex = null, int? TableIndex = null);

public sealed record DocumentReadIssue(string Code, string Message);

public sealed record DocumentReadResult(
    DocumentModel? Document,
    IReadOnlyList<DocumentReadIssue> Issues)
{
    public bool IsSuccess => Document is not null && Issues.Count == 0;

    public static DocumentReadResult Failure(string code, string message) =>
        new(null, [new DocumentReadIssue(code, message)]);
}
