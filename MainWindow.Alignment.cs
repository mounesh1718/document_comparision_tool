using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine;

/// <summary>
/// Keeps the two documents line-aligned like a diff viewer: matching content starts at the same height in
/// both panes, content missing on one side leaves a highlighted gap of the same size, table rows share their
/// heights, and both panes scroll by the same amount.
/// </summary>
public partial class MainWindow
{
    // Content that has a counterpart in the other pane, in document order. The same key on both sides marks the
    // same content: "P:n" = Reference paragraph n (or its Current counterpart), "C:n" = Current-only paragraph n,
    // "T:part:n" = n-th table of a part, "PART:key" = a header, footer or note.
    private readonly List<(string Key, Control Control)> _referenceSync = [];
    private readonly List<(string Key, Control Control)> _currentSync = [];
    private readonly HashSet<Control> _placeholders = [];
    private readonly Dictionary<string, Grid> _referenceTableGrids = [];
    private readonly Dictionary<string, Grid> _currentTableGrids = [];
    private bool _panesAligned;

    /// <summary>A table's shared key, the row list both panes use, and the paired table in the other document.</summary>
    private sealed record TableAlignmentInfo(string Key, IReadOnlyList<(int? Reference, int? Current)> Rows, TableDisplayBlock Other);

    /// <summary>Alignment needs the engine's paragraph pairing; results built without it keep the old layout.</summary>
    private bool CanAlign => _comparisonResult is { } result &&
        (result.ParagraphPairs is not null || result.Status == ComparisonStatus.ExactFileMatch);

    private void ResetAlignment()
    {
        _referenceSync.Clear();
        _currentSync.Clear();
        _placeholders.Clear();
        _referenceTableGrids.Clear();
        _currentTableGrids.Clear();
        _panesAligned = false;
        ReferenceDocumentPanel.MinHeight = 0;
        CurrentDocumentPanel.MinHeight = 0;
    }

    private void RegisterSync(bool isReference, string key, Control control, bool isPlaceholder = false)
    {
        (isReference ? _referenceSync : _currentSync).Add((key, control));
        if (isPlaceholder)
            _placeholders.Add(control);
    }

    private string ParagraphKey(int paragraphIndex, bool isReference)
    {
        if (isReference)
            return $"P:{paragraphIndex}";
        return _comparisonResult!.CounterpartOf(paragraphIndex, isReference: false) is { } reference
            ? $"P:{reference}"
            : $"C:{paragraphIndex}";
    }

    /// <summary>"T:n" = Reference table n or its Current counterpart, "TC:n" = Current-only table n.</summary>
    private string TableSyncKey(int tableIndex, bool isReference)
    {
        if (isReference)
            return $"T:{tableIndex}";
        return _comparisonResult!.CounterpartTableOf(tableIndex, isReference: false) is { } reference
            ? $"T:{reference}"
            : $"TC:{tableIndex}";
    }

    /// <summary>A whole table present on one side only (as opposed to a changed table).</summary>
    private static bool IsOneSidedTable(DocumentDifference difference) =>
        difference.Category == DifferenceCategory.Table && difference.Property == "Table" &&
        (difference.ReferenceTableIndex is null) != (difference.CurrentTableIndex is null);

    /// <summary>The key of the paragraph or table a "missing here" placeholder stands in for.</summary>
    private string MissingKey(DocumentDifference difference)
    {
        if (IsOneSidedTable(difference))
            return difference.ReferenceTableIndex is { } removed
                ? TableSyncKey(removed, isReference: true)
                : TableSyncKey(difference.CurrentTableIndex!.Value, isReference: false);
        return difference.Category == DifferenceCategory.ParagraphRemoved
            ? $"P:{(difference.ReferenceRange?.ParagraphIndex ?? difference.ReferenceLocation!.ParagraphIndex)}"
            : $"C:{(difference.CurrentRange?.ParagraphIndex ?? difference.CurrentLocation!.ParagraphIndex)}";
    }

    /// <summary>Top-level blocks (paragraphs and tables, including inside parts) with their shared keys, in display order.</summary>
    private List<(string Key, string? Part, int? ParagraphIndex, int? TableIndex)> BlockSequence(DocumentModel document, bool isReference)
    {
        var sequence = new List<(string, string?, int?, int?)>();
        void Collect(IEnumerable<DocumentDisplayBlock> blocks, string? partKey)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case PartDisplayBlock part:
                        Collect(part.Blocks, part.Part.Key);
                        break;
                    case ParagraphDisplayBlock paragraph:
                        sequence.Add((ParagraphKey(paragraph.Paragraph.Index, isReference), partKey, paragraph.Paragraph.Index, null));
                        break;
                    case TableDisplayBlock table:
                        sequence.Add((TableSyncKey(table.TableIndex, isReference), partKey, null, table.TableIndex));
                        break;
                }
            }
        }
        Collect(document.DisplayBlocks, null);
        return sequence;
    }

    /// <summary>
    /// Places each gap for content that exists only in the other document: before the first block that follows
    /// that content in the other document and also exists here (a paragraph or a table), or at the end of its part.
    /// </summary>
    private (ILookup<string, DocumentDifference> Before, ILookup<string?, DocumentDifference> AtEnd) PlaceMissing(
        IReadOnlyList<DocumentDifference> missing, DocumentModel document, DocumentModel otherDocument, bool isReference)
    {
        var here = BlockSequence(document, isReference).Select(block => block.Key).ToHashSet();
        var other = BlockSequence(otherDocument, !isReference);
        var before = new List<(string Key, DocumentDifference Difference)>();
        var atEnd = new List<(string? Part, DocumentDifference Difference)>();
        // Gaps follow the order of the missing content in the other document, so added paragraphs
        // and added tables are interleaved exactly as they are there.
        var positioned = missing.Select(difference =>
        {
            var location = isReference ? difference.CurrentLocation : difference.ReferenceLocation;
            var otherTable = isReference ? difference.CurrentTableIndex : difference.ReferenceTableIndex;
            var position = IsOneSidedTable(difference)
                ? other.FindIndex(block => block.TableIndex == otherTable)
                : other.FindIndex(block => block.ParagraphIndex == location?.ParagraphIndex);
            return (Difference: difference, Start: position);
        }).OrderBy(item => item.Start < 0 ? int.MaxValue : item.Start).ToList();
        foreach (var (difference, start) in positioned)
        {
            if (start < 0)
            {
                atEnd.Add((SourcePart(difference, otherDocument, isReference)?.Key, difference));
                continue;
            }
            var part = other[start].Part;
            var next = other.Skip(start + 1).TakeWhile(block => block.Part == part).FirstOrDefault(block => here.Contains(block.Key));
            if (next.Key is not null)
                before.Add((next.Key, difference));
            else
                atEnd.Add((part, difference));
        }
        return (before.ToLookup(item => item.Key, item => item.Difference), atEnd.ToLookup(item => item.Part, item => item.Difference));
    }

    /// <summary>
    /// Rows of a table pair in display order. Each entry is a Reference row, a Current row, or both;
    /// a row present on one side only becomes a placeholder row on the other side.
    /// </summary>
    private IReadOnlyList<(int? Reference, int? Current)> AlignedRows(TableDisplayBlock reference, TableDisplayBlock current)
    {
        var currentRowOf = _comparisonResult!.CurrentDocument!.Paragraphs
            .Where(paragraph => paragraph.TableLocation?.TableIndex == current.TableIndex)
            .ToDictionary(paragraph => paragraph.Index, paragraph => paragraph.TableLocation!.RowIndex);
        var pairs = new List<(int Reference, int Current)>();
        for (var row = 0; row < reference.Rows.Count; row++)
        {
            var match = reference.Rows[row].Cells.SelectMany(cell => cell.Paragraphs)
                .Select(paragraph => _comparisonResult.CounterpartOf(paragraph.Index, isReference: true))
                .OfType<int>()
                .Select(index => currentRowOf.TryGetValue(index, out var currentRow) ? currentRow : (int?)null)
                .FirstOrDefault(currentRow => currentRow is not null);
            if (match is { } currentRow && (pairs.Count == 0 || currentRow > pairs[^1].Current))
                pairs.Add((row, currentRow));
        }

        var rows = new List<(int?, int?)>();
        int left = 0, right = 0;
        foreach (var (referenceRow, currentRow) in pairs.Append((reference.Rows.Count, current.Rows.Count)))
        {
            while (left < referenceRow)
                rows.Add((left++, null));
            while (right < currentRow)
                rows.Add((null, right++));
            if (referenceRow < reference.Rows.Count)
                rows.Add((left++, right++));
        }
        return rows;
    }

    /// <summary>A pink row standing in for a table row that exists only in the other document.</summary>
    private Control CreateMissingRow(TableDisplayBlock otherTable, int otherRow, bool isReference, IDictionary<string, Control> anchors)
    {
        var values = string.Join(" · ", otherTable.Rows[otherRow].Cells.Select(cell => string.Join(" ", cell.Paragraphs.Select(paragraph => paragraph.Text))));
        var differences = _comparisonResult!.Differences.Where(difference => isReference
                ? difference.Category == DifferenceCategory.ParagraphAdded &&
                  difference.CurrentLocation?.TableCell is { } added && added.TableIndex == otherTable.TableIndex && added.RowIndex == otherRow
                : difference.Category == DifferenceCategory.ParagraphRemoved &&
                  difference.ReferenceLocation?.TableCell is { } removed && removed.TableIndex == otherTable.TableIndex && removed.RowIndex == otherRow)
            .ToList();
        var label = new TextBlock
        {
            Text = isReference ? $"Row added in Current: {ShortLabel(values)}" : $"Row missing here: {ShortLabel(values)}",
            FontStyle = FontStyle.Italic,
            FontSize = 12 * _documentZoom,
            Foreground = new SolidColorBrush(Color.Parse("#9D174D")),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        return CreateSideBar(label, differences, anchors, MarkerStyle.Missing,
            isReference ? "Current has a table row here that the Reference does not." : "The Reference has a table row here that is missing from Current.");
    }

    private void ScheduleAlignment()
    {
        if (!CanAlign)
            return;
        // Background priority runs after layout, when every block has its real height.
        Dispatcher.UIThread.Post(AlignPanes, DispatcherPriority.Background);
    }

    private void AlignPanes()
    {
        if (_closed || !CanAlign || _panesAligned)
            return;
        _panesAligned = true;

        // 1. Table rows and "missing here" gaps take the height of their counterpart.
        foreach (var (key, referenceGrid) in _referenceTableGrids)
        {
            if (!_currentTableGrids.TryGetValue(key, out var currentGrid) ||
                referenceGrid.RowDefinitions.Count != currentGrid.RowDefinitions.Count)
                continue;
            for (var row = 0; row < referenceGrid.RowDefinitions.Count; row++)
            {
                var rowHeight = Math.Max(referenceGrid.RowDefinitions[row].ActualHeight, currentGrid.RowDefinitions[row].ActualHeight);
                referenceGrid.RowDefinitions[row].MinHeight = rowHeight;
                currentGrid.RowDefinitions[row].MinHeight = rowHeight;
            }
        }
        var currentByKey = _currentSync.Select((item, order) => (item.Key, item.Control, Order: order))
            .GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.First());
        foreach (var (key, referenceControl) in _referenceSync)
        {
            if (!currentByKey.TryGetValue(key, out var current))
                continue;
            if (_placeholders.Contains(referenceControl))
                referenceControl.MinHeight = current.Control.Bounds.Height;
            else if (_placeholders.Contains(current.Control))
                current.Control.MinHeight = referenceControl.Bounds.Height;
        }
        ReferenceDocumentPanel.UpdateLayout();

        // 2. Each matched block starts at the same height: space is added above the block that is higher.
        // Space added to a block moves everything after it on that side, so the shifts are accumulated.
        // Only blocks in the same order on both sides can line up; the largest such set is used.
        double referenceShift = 0, currentShift = 0;
        foreach (var (referenceControl, current) in InSameOrder(_referenceSync
                     .Where(item => currentByKey.ContainsKey(item.Key))
                     .Select(item => (item.Control, currentByKey[item.Key]))
                     .ToList()))
        {
            if (referenceControl.TranslatePoint(default, ReferenceDocumentPanel) is not { } referenceTop ||
                current.Control.TranslatePoint(default, CurrentDocumentPanel) is not { } currentTop)
                continue;
            var gap = referenceTop.Y + referenceShift - (currentTop.Y + currentShift);
            if (Math.Abs(gap) < 0.5)
                continue;
            if (gap > 0)
            {
                AddSpaceAbove(current.Control, gap);
                currentShift += gap;
            }
            else
            {
                AddSpaceAbove(referenceControl, -gap);
                referenceShift -= gap;
            }
        }
        ReferenceDocumentPanel.UpdateLayout();

        // 3. Both pages end at the same height, so one scroll offset fits both.
        var height = Math.Max(ReferenceDocumentPanel.Bounds.Height, CurrentDocumentPanel.Bounds.Height);
        ReferenceDocumentPanel.MinHeight = height;
        CurrentDocumentPanel.MinHeight = height;
        ScheduleOverviewUpdate();
    }

    /// <summary>
    /// The largest set of matched blocks whose Current order increases with their Reference order
    /// (longest increasing subsequence), so one crossing match cannot block the blocks after it.
    /// </summary>
    private static List<(Control Reference, (string Key, Control Control, int Order) Current)> InSameOrder(
        List<(Control Reference, (string Key, Control Control, int Order) Current)> matches)
    {
        var tails = new List<int>();          // tails[k]: index of the smallest last order of an increasing run of length k + 1
        var previous = new int[matches.Count];
        for (var index = 0; index < matches.Count; index++)
        {
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (matches[tails[middle]].Current.Order < matches[index].Current.Order)
                    low = middle + 1;
                else
                    high = middle;
            }
            previous[index] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count)
                tails.Add(index);
            else
                tails[low] = index;
        }
        var result = new List<(Control, (string, Control, int))>();
        for (var index = tails.Count > 0 ? tails[^1] : -1; index >= 0; index = previous[index])
            result.Add(matches[index]);
        result.Reverse();
        return result;
    }

    private static void AddSpaceAbove(Control control, double space) =>
        control.Margin = new Thickness(control.Margin.Left, control.Margin.Top + space, control.Margin.Right, control.Margin.Bottom);
}
