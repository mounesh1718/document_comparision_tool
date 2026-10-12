using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine;

/// <summary>
/// Clicking content without any finding: both documents outline the paired paragraphs in blue and the
/// details panel confirms that Current matches the Reference there.
/// </summary>
public partial class MainWindow
{
    private const string MatchAnchor = "MATCH";
    private MatchSelection? _selectedMatch;
    private ComparisonResult? _findingIndexSource;
    private HashSet<int> _referenceFindingParagraphs = [];
    private HashSet<int> _currentFindingParagraphs = [];

    private sealed record MatchSelection(int? ReferenceIndex, int? CurrentIndex);

    /// <summary>True when any finding points at this paragraph, so its click belongs to that finding.</summary>
    private bool HasAnyFinding(int paragraphIndex, bool isReference)
    {
        if (!ReferenceEquals(_findingIndexSource, _comparisonResult))
        {
            _findingIndexSource = _comparisonResult;
            var differences = _comparisonResult?.Differences ?? [];
            _referenceFindingParagraphs = Paragraphs(differences, isReference: true);
            _currentFindingParagraphs = Paragraphs(differences, isReference: false);
        }
        return (isReference ? _referenceFindingParagraphs : _currentFindingParagraphs).Contains(paragraphIndex);

        static HashSet<int> Paragraphs(IEnumerable<DocumentDifference> differences, bool isReference) => differences
            .SelectMany(difference => new[]
            {
                (isReference ? difference.ReferenceLocation : difference.CurrentLocation)?.ParagraphIndex,
                (isReference ? difference.ReferenceRange : difference.CurrentRange)?.ParagraphIndex
            })
            .OfType<int>()
            .ToHashSet();
    }

    private void SelectMatch(int paragraphIndex, bool isReference)
    {
        if (_comparisonResult?.ReferenceDocument is not { } referenceDocument || _comparisonResult.CurrentDocument is not { } currentDocument)
            return;

        var counterpart = _comparisonResult.CounterpartOf(paragraphIndex, isReference);
        var selection = isReference ? new MatchSelection(paragraphIndex, counterpart) : new MatchSelection(counterpart, paragraphIndex);
        _selectedDifferenceId = null;
        // Clear the list's selection before its items change, so the list cannot select a difference again;
        // a narrowed list from an earlier click is dropped, but a filter chosen in the drop-down stays.
        DifferenceList.SelectedItem = null;
        if (FindingFilter.SelectedIndex == 0)
            ShowNavigatorItems(_navigatorItems, null);
        DifferenceList.SelectedItem = null;
        _selectedMatch = selection;
        SelectionScopeText.Text = "Selected content · no difference here";

        var referenceParagraph = Find(referenceDocument, selection.ReferenceIndex);
        var currentParagraph = Find(currentDocument, selection.CurrentIndex);
        ShowMatchDetails(referenceParagraph, currentParagraph);

        RenderDocuments();
        UpdateReviewButton();
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_selectedMatch, selection) || _closed)
                return;
            // Each pane scrolls to its own copy of the content, like a selected difference.
            _synchronizingScroll = true;
            try
            {
                if (_referenceAnchors.TryGetValue(MatchAnchor, out var referenceAnchor))
                    referenceAnchor.BringIntoView();
                if (_currentAnchors.TryGetValue(MatchAnchor, out var currentAnchor))
                    currentAnchor.BringIntoView();
            }
            finally { _synchronizingScroll = false; }
        }, DispatcherPriority.Background);

        static ParagraphBlock? Find(DocumentModel document, int? index) =>
            index is null ? null : document.Paragraphs.FirstOrDefault(paragraph => paragraph.Index == index);
    }

    private void ShowMatchDetails(ParagraphBlock? reference, ParagraphBlock? current)
    {
        ClearDetails();
        DetailScrollViewer.Offset = new Avalonia.Vector(0, 0);
        DetailMoreExpander.IsExpanded = false;
        var paired = reference is not null && current is not null;

        DetailOperationText.Text = paired ? "No difference" : "Not paired";
        DetailConfidenceText.Text = paired
            ? "MATCH — Current is the same as the approved Reference here."
            : "NOT PAIRED — check this content in the original documents.";
        DetailConfidenceText.Foreground = new SolidColorBrush(Color.Parse(paired ? "#166534" : "#92400E"));
        DetailConfidenceText.Background = new SolidColorBrush(Color.Parse(paired ? "#F0FDF4" : "#FFFBEB"));
        DetailConfidenceText.IsVisible = true;
        ShowDetails();
        ShowOnPagesButton.IsVisible = false;
        DetailSourceTruthText.Text = paired
            ? "Reference and Current have the same text, spacing and direct formatting here. No action is needed." + TableNote(reference!, current!)
            : "This content was not compared with a specific paragraph in the other document, for example because it is part of an image/table replacement.";
        DetailReferenceText.Text = MatchText(reference);
        DetailCurrentText.Text = MatchText(current);

        var match = new DocumentDifference("MATCH", DifferenceCategory.Text, "Same content", DetailReferenceText.Text, DetailCurrentText.Text,
            Location(reference), Location(current), null, null, string.Empty);
        DetailLocationText.Text = DescribeLocation(match);
        DetailPageNoteText.Text = PageNote(match);
        DetailPageNoteText.IsVisible = !string.IsNullOrWhiteSpace(DetailPageNoteText.Text);
        DetailCategoryText.Text = "Compared: text, spaces, direct formatting and paragraph settings.";
        DetailActionText.Text = paired ? "Recommended action: none." : "Recommended action: compare this content manually.";

        AddCrop(DetailReferenceImage, DetailReferenceImageFrame, FirstImage(_comparisonResult?.ReferenceDocument, reference), null);
        AddCrop(DetailCurrentImage, DetailCurrentImageFrame, FirstImage(_comparisonResult?.CurrentDocument, current), null);

        static DocumentLocation? Location(ParagraphBlock? paragraph) => paragraph is null
            ? null
            : new DocumentLocation(paragraph.Index, null, paragraph.TableLocation, paragraph.PageAt(0));
    }

    /// <summary>Unchanged cell text can still sit in a table whose structure changed; say so.</summary>
    private string TableNote(ParagraphBlock reference, ParagraphBlock current)
    {
        var referenceTable = reference.TableLocation?.TableIndex;
        var currentTable = current.TableLocation?.TableIndex;
        if (referenceTable is null && currentTable is null)
            return string.Empty;
        var tableFindings = _comparisonResult!.Differences.Count(difference =>
            referenceTable is not null && difference.ReferenceTableIndex == referenceTable ||
            currentTable is not null && difference.CurrentTableIndex == currentTable);
        return tableFindings == 0 ? string.Empty : $" This table has {tableFindings} other finding(s) in the list.";
    }

    private static string MatchText(ParagraphBlock? paragraph)
    {
        if (paragraph is null)
            return "Not paired";
        if (paragraph.Text.Length > 0)
            return paragraph.Text;
        return paragraph.Runs.Any(run => run.ImageReferences.Count > 0) ? "(image)" : "(empty paragraph)";
    }

    private static DocumentImage? FirstImage(DocumentModel? document, ParagraphBlock? paragraph)
    {
        var imageId = paragraph?.Runs.SelectMany(run => run.ImageReferences).FirstOrDefault();
        return imageId is null ? null : document?.ImageItems.FirstOrDefault(image => image.Id == imageId);
    }
}
