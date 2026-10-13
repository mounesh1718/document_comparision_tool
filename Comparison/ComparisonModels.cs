using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.Comparison;

public enum ComparisonStatus
{
    InvalidInput,
    ExactFileMatch,
    DocumentDataMatch,
    Different
}

public enum DifferenceCategory
{
    Text,
    Formatting,
    ParagraphAdded,
    ParagraphRemoved,
    Table,
    Image,
    Review
}

/// <summary>The visible form of a finding on one side of the comparison.</summary>
public enum DifferenceContentKind
{
    Text,
    Table,
    Image,
    Missing
}

public sealed record DocumentRange(int ParagraphIndex, int Start, int Length);
public sealed record ImageDifferenceRegion(int X, int Y, int Width, int Height);

public sealed record DocumentLocation(int ParagraphIndex, int? RunIndex, TableCellLocation? TableCell = null, DocumentPage? Page = null)
{
    public string PageDisplay => Page?.Display ?? "Page unavailable";
    public string Display
    {
        get
        {
            var location = RunIndex is null
                ? $"{PageDisplay}, Paragraph {ParagraphIndex + 1}"
                : $"{PageDisplay}, Paragraph {ParagraphIndex + 1}, Run {RunIndex + 1}";
            return TableCell is null
                ? location
                : $"{location}, Table {TableCell.TableIndex + 1}, Row {TableCell.RowIndex + 1}, Cell {TableCell.CellIndex + 1}";
        }
    }
}

public sealed record DocumentDifference(
    string Id,
    DifferenceCategory Category,
    string Property,
    string ReferenceValue,
    string CurrentValue,
    DocumentLocation? ReferenceLocation,
    DocumentLocation? CurrentLocation,
    DocumentRange? ReferenceRange,
    DocumentRange? CurrentRange,
    string NearbyText,
    string? ReferenceImageId = null,
    string? CurrentImageId = null,
    int? ReferenceTableIndex = null,
    int? CurrentTableIndex = null,
    ImageDifferenceRegion? ImageRegion = null,
    ImageDifferenceRegion? CurrentImageRegion = null,
    bool IsRecognizedText = false,
    string? ReviewNote = null,
    DifferenceContentKind ReferenceContentKind = DifferenceContentKind.Text,
    DifferenceContentKind CurrentContentKind = DifferenceContentKind.Text,
    int? CounterpartParagraphIndex = null)
{
    // For an added/removed body paragraph: the paragraph on the other side before which the
    // missing content belongs. EndOfDocument means after the last paragraph.
    public const int EndOfDocument = -1;

    public string Summary => $"{Id} {Category}: {Property}";
    public ImageDifferenceRegion? RegionFor(bool isReference) => isReference ? ImageRegion : CurrentImageRegion ?? ImageRegion;
}

/// <summary>What a comparison includes. The reviewer chooses this before comparing.</summary>
public sealed record ComparisonOptions(bool CompareHeadersAndFooters = true)
{
    public static ComparisonOptions Default { get; } = new();
}

public sealed record ComparisonResult(
    ComparisonStatus Status,
    DocumentModel? ReferenceDocument,
    DocumentModel? CurrentDocument,
    IReadOnlyList<DocumentDifference> Differences,
    IReadOnlyList<DocumentReadIssue> Issues,
    string? ReferenceHash,
    string? CurrentHash,
    IReadOnlyList<ParagraphPair>? ParagraphPairs = null,
    IReadOnlyList<TablePair>? TablePairs = null,
    ComparisonOptions? Options = null)
{
    public bool IsSuccess => Status != ComparisonStatus.InvalidInput;

    /// <summary>The options the comparison used (all content when none were given).</summary>
    public ComparisonOptions UsedOptions => Options ?? ComparisonOptions.Default;

    /// <summary>The table on the other side that a table was compared with, or null when it has none.</summary>
    public int? CounterpartTableOf(int tableIndex, bool isReference)
    {
        if (Status == ComparisonStatus.ExactFileMatch)
            return tableIndex;
        var pair = TablePairs?.FirstOrDefault(item =>
            (isReference ? item.ReferenceTableIndex : item.CurrentTableIndex) == tableIndex);
        return pair is null ? null : isReference ? pair.CurrentTableIndex : pair.ReferenceTableIndex;
    }

    /// <summary>
    /// The paragraph on the other side that a paragraph was compared with, or null when it has none.
    /// Identical files share their structure, so each paragraph matches the same index.
    /// </summary>
    public int? CounterpartOf(int paragraphIndex, bool isReference)
    {
        if (Status == ComparisonStatus.ExactFileMatch)
            return paragraphIndex;
        var pair = ParagraphPairs?.FirstOrDefault(item =>
            (isReference ? item.ReferenceParagraphIndex : item.CurrentParagraphIndex) == paragraphIndex);
        return pair is null ? null : isReference ? pair.CurrentParagraphIndex : pair.ReferenceParagraphIndex;
    }
}

/// <summary>A Reference paragraph and the Current paragraph it was compared with (same or edited).</summary>
public sealed record ParagraphPair(int ReferenceParagraphIndex, int CurrentParagraphIndex);

/// <summary>A Reference table and the Current table it was compared with (same or edited).</summary>
public sealed record TablePair(int ReferenceTableIndex, int CurrentTableIndex);
