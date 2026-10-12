using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using DocumentChecker.Engine.Reporting;

namespace DocumentChecker.Engine;

public partial class MainWindow : Window
{
    private readonly DocumentComparisonService _comparisonService = new();
    private readonly ComparisonReportGenerator _reportGenerator = new();
    private readonly Dictionary<string, Control> _referenceAnchors = new();
    private readonly Dictionary<string, Control> _currentAnchors = new();
    private string? _referencePath;
    private string? _currentPath;
    private string? _selectedDifferenceId;
    private ComparisonResult? _comparisonResult;
    private IReadOnlyList<NavigatorItem> _navigatorItems = [];
    private FileSystemWatcher? _referenceWatcher;
    private FileSystemWatcher? _currentWatcher;
    private CancellationTokenSource? _autoRefreshCancellation;
    private double _documentZoom = 1;
    private bool _synchronizingScroll;
    private bool _comparing;
    private bool _closed;
    private bool _showChangeMarkers = true;
    private readonly List<Bitmap> _documentBitmaps = [];
    private readonly List<Bitmap> _detailBitmaps = [];

    public MainWindow()
    {
        InitializeComponent();
        HookOverviewUpdates();
        ShowWelcome();
    }

    /// <summary>Before the first comparison, each page explains what belongs there and what to do next.</summary>
    private void ShowWelcome()
    {
        ReferenceDocumentPanel.Children.Add(CreateGuide("#9F1239", "The approved document goes here",
            "This is the Reference: the version that was signed off. Choose it with ① above."));
        CurrentDocumentPanel.Children.Add(CreateGuide("#166534", "The document to check goes here",
            "This is the Current version. Choose it with ②, then click Compare. Every difference is highlighted here, " +
            "next to the same place in the approved document, and listed on the right."));
    }

    private static Control CreateGuide(string color, string title, string text) => new StackPanel
    {
        Spacing = 8,
        Margin = new Thickness(8, 40),
        Children =
        {
            new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse(color)), TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = text, FontSize = 14, Foreground = new SolidColorBrush(Color.Parse("#475569")), TextWrapping = TextWrapping.Wrap }
        }
    };

    /// <summary>Shows the file name of a chosen document; the full path is in its tooltip.</summary>
    private static void ShowChosenFile(TextBlock target, string? path)
    {
        target.Text = string.IsNullOrWhiteSpace(path) ? "Not chosen yet" : Path.GetFileName(path);
        ToolTip.SetTip(target, path);
    }

    private async void SelectReference_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _referencePath = await SelectDocxAsync("Select the approved Reference DOCX");
        ShowChosenFile(ReferencePathText, _referencePath);
        ConfigureWatcher(isReference: true, _referencePath);
        UpdateButtons();
    }

    private async void SelectCurrent_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _currentPath = await SelectDocxAsync("Select the Current DOCX to verify");
        ShowChosenFile(CurrentPathText, _currentPath);
        ConfigureWatcher(isReference: false, _currentPath);
        UpdateButtons();
    }

    private async void Compare_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await RunComparisonAsync(automatic: false);
    }

    private async Task RunComparisonAsync(bool automatic)
    {
        if (_comparing || _closed)
        {
            if (automatic && !_closed)
                ScheduleAutoRefresh();
            return;
        }
        _comparing = true;
        UpdateButtons();
        StatusText.Text = "Comparing the two documents…";
        var referencePath = _referencePath;
        var currentPath = _currentPath;
        try
        {
            var result = await Task.Run(() => _comparisonService.Compare(referencePath, currentPath));
            if (_closed || referencePath != _referencePath || currentPath != _currentPath)
                return;
            _comparisonResult = result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText.Text = "Comparison could not finish. Check the selected documents and try again.";
            return;
        }
        finally
        {
            _comparing = false;
            UpdateButtons();
        }
        _selectedDifferenceId = null;
        _selectedMatch = null;
        UpdateButtons();

        if (!_comparisonResult.IsSuccess)
        {
            StatusText.Text = automatic
                ? "A selected document is still being saved. The comparison will refresh automatically."
                : "Comparison is waiting for an unavailable document. " + string.Join(" ", _comparisonResult.Issues.Select(issue => issue.Message));
            ShowReadFailure();
            if (automatic)
                ScheduleAutoRefresh();
            return;
        }

        var reviewCount = _comparisonResult.Differences.Count(RequiresReview);
        StatusText.Text = _comparisonResult.Status switch
        {
            ComparisonStatus.ExactFileMatch => "✓ Exact File Match: the two files are identical, byte for byte.",
            ComparisonStatus.DocumentDataMatch => "✓ No differences found in the checked content. See what is checked at the bottom.",
            _ => $"{Plural(_comparisonResult.Differences.Count, "difference")} found" +
                 (reviewCount == 0 ? string.Empty : $" · {reviewCount} to check yourself")
        };
        LegendPanel.IsVisible = _comparisonResult.Status == ComparisonStatus.Different;

        _navigatorItems = BuildNavigatorItems(_comparisonResult.Differences);
        ShowNavigatorItems(_navigatorItems, null);
        ReferenceDocumentName.Text = _comparisonResult.ReferenceDocument?.FileName ?? "No document selected";
        CurrentDocumentName.Text = _comparisonResult.CurrentDocument?.FileName ?? "No document selected";
        RenderDocuments();
        ClearDetails();
        UpdateReviewButton();

        // Open on the first real change rather than the top of the page. An automatic refresh after a
        // save keeps the reviewer where they are.
        if (!automatic && FirstChangeInDocument() is { } firstChange)
            SelectNavigatorDifference(firstChange);
    }

    /// <summary>
    /// The real change (not a review item) that comes first when reading the Current document. Changes that
    /// exist only in the Reference are placed by the Current paragraph their Reference paragraph pairs with.
    /// </summary>
    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private DocumentDifference? FirstChangeInDocument() => _comparisonResult!.Differences
        .Where(difference => !RequiresReview(difference))
        .OrderBy(difference => difference.CurrentLocation?.ParagraphIndex
            ?? (difference.ReferenceLocation is { } reference
                ? _comparisonResult.CounterpartOf(reference.ParagraphIndex, isReference: true) ?? reference.ParagraphIndex
                : int.MaxValue))
        .FirstOrDefault();

    private void ConfigureWatcher(bool isReference, string? path)
    {
        var existing = isReference ? _referenceWatcher : _currentWatcher;
        existing?.Dispose();

        FileSystemWatcher? watcher = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Changed += DocumentFileChanged;
            watcher.Created += DocumentFileChanged;
            watcher.Renamed += DocumentFileRenamed;
        }

        if (isReference)
            _referenceWatcher = watcher;
        else
            _currentWatcher = watcher;
    }

    private void DocumentFileChanged(object sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(ScheduleAutoRefresh);

    private void DocumentFileRenamed(object sender, RenamedEventArgs e) => Dispatcher.UIThread.Post(ScheduleAutoRefresh);

    private void ScheduleAutoRefresh()
    {
        if (_closed || string.IsNullOrWhiteSpace(_referencePath) || string.IsNullOrWhiteSpace(_currentPath))
            return;

        _autoRefreshCancellation?.Cancel();
        _autoRefreshCancellation?.Dispose();
        _autoRefreshCancellation = new CancellationTokenSource();
        var cancellation = _autoRefreshCancellation.Token;
        _ = RefreshAfterSaveAsync(cancellation);
    }

    private async Task RefreshAfterSaveAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellation);
            if (cancellation.IsCancellationRequested)
                return;
            await Dispatcher.UIThread.InvokeAsync(() => { _ = RunComparisonAsync(automatic: true); });
        }
        catch (OperationCanceledException)
        {
            // A newer document-save event replaced this refresh request.
        }
    }

    private async void GeneratePdf_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_comparisonResult is null || !_comparisonResult.IsSuccess)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save DOCX comparison report",
            SuggestedFileName = "DOCX-Comparison-Report.pdf",
            FileTypeChoices = [new FilePickerFileType("PDF report") { Patterns = ["*.pdf"] }]
        });
        var outputPath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        try
        {
            _reportGenerator.Generate(_comparisonResult, outputPath);
            StatusText.Text = "PDF report created successfully.";
        }
        catch (Exception)
        {
            StatusText.Text = "The PDF report could not be created.";
        }
    }

    private void DifferenceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DifferenceList.SelectedItem is NavigatorItem item)
            SelectDifference(item.Difference);
    }

    private void PreviousDifference_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => MoveSelection(-1);

    private void NextDifference_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => MoveSelection(1);

    private void ZoomOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetZoom(_documentZoom - 0.1);

    private void ZoomIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetZoom(_documentZoom + 0.1);

    private void HighlightToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _showChangeMarkers = !_showChangeMarkers;
        HighlightToggleButton.Content = _showChangeMarkers ? "Hide highlights" : "Show highlights";
        RenderDocuments();
    }

    private void ReferenceScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        SynchronizeScroll(ReferenceScrollViewer, CurrentScrollViewer);
        UpdateViewportIndicators();
    }

    private void CurrentScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        SynchronizeScroll(CurrentScrollViewer, ReferenceScrollViewer);
        UpdateViewportIndicators();
    }

    private async Task<string?> SelectDocxAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Word document") { Patterns = ["*.docx"] }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void SelectDifference(DocumentDifference difference)
    {
        if (_comparisonResult is null)
            return;

        _selectedDifferenceId = difference.Id;
        // Leaving a "no difference" selection: restore the list label unless the list is narrowed.
        if (_selectedMatch is not null && !ShowAllChangesButton.IsVisible)
            SelectionScopeText.Text = "All changes · click a highlighted value to focus";
        _selectedMatch = null;
        DetailScrollViewer.Offset = new Vector(0, 0);
        DetailMoreExpander.IsExpanded = false;
        var item = FindNavigatorItem(difference);
        DetailOperationText.Text = item?.Operation ?? GetOperation(difference);
        DetailCategoryText.Text = difference.Property;
        var referenceKind = GetStructureKind(difference, true);
        var currentKind = GetStructureKind(difference, false);
        var mixedRepresentation = referenceKind != currentKind;
        DetailContentKindGrid.IsVisible = difference.Category != DifferenceCategory.Review;
        DetailReferenceContentKindText.Text = $"Reference structure: {ContentKindLabel(referenceKind)}";
        DetailCurrentContentKindText.Text = $"Current structure: {ContentKindLabel(currentKind)}";
        var needsReview = RequiresReview(difference);
        DetailConfidenceText.Text = needsReview ? "REVIEW REQUIRED — check the original evidence." : "READ FROM DOCX — check the paired values and locations.";
        DetailConfidenceText.Foreground = new SolidColorBrush(Color.Parse(needsReview ? "#92400E" : "#166534"));
        DetailConfidenceText.Background = new SolidColorBrush(Color.Parse(needsReview ? "#FFFBEB" : "#F0FDF4"));
        DetailConfidenceText.IsVisible = needsReview;
        DetailSourceTruthText.Text = BuildSourceTruthExplanation(difference);
        DetailActionText.Text = BuildUserAction(difference);
        DetailRepresentationText.Text = mixedRepresentation ? DescribeRepresentation(difference) : string.Empty;
        DetailRepresentationText.IsVisible = mixedRepresentation;
        DetailLocationText.Text = DescribeLocation(difference);
        DetailPageNoteText.Text = PageNote(difference);
        DetailPageNoteText.IsVisible = !string.IsNullOrWhiteSpace(DetailPageNoteText.Text);
        DetailReferenceText.Text = DetailValue(difference, true);
        DetailCurrentText.Text = DetailValue(difference, false);
        DetailContextText.Text = difference.NearbyText;
        DetailReviewText.Text = needsReview ? "Why review is required: " + (difference.ReviewNote ?? "This document part needs manual verification.") : string.Empty;
        DetailReviewText.IsVisible = needsReview;
        UpdateImageDetails(difference);
        ShowDetails();
        var position = _navigatorItems.ToList().FindIndex(navigatorItem => navigatorItem.Difference.Id == difference.Id);
        PositionText.Text = position < 0 ? string.Empty : $"{position + 1} of {_navigatorItems.Count}";
        if (item is not null)
            DifferenceList.ScrollIntoView(item);
        RenderDocuments();
        UpdateReviewButton();

        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedDifferenceId != difference.Id || _closed) return;
            // Selection navigates to each side independently; proportional scrolling must not override its paired anchor.
            _synchronizingScroll = true;
            try
            {
            if (_referenceAnchors.TryGetValue(difference.Id, out var referenceAnchor))
                referenceAnchor.BringIntoView();
            if (_currentAnchors.TryGetValue(difference.Id, out var currentAnchor))
                currentAnchor.BringIntoView();
            }
            finally { _synchronizingScroll = false; }
        }, DispatcherPriority.Background);
    }

    private void MoveSelection(int direction)
    {
        var differences = _comparisonResult?.Differences;
        if (differences is null || differences.Count == 0)
            return;

        var currentIndex = _selectedDifferenceId is null
            ? -1
            : differences.ToList().FindIndex(item => item.Id == _selectedDifferenceId);
        var nextIndex = currentIndex < 0
            ? (direction > 0 ? 0 : differences.Count - 1)
            : (currentIndex + direction + differences.Count) % differences.Count;
        var difference = differences[nextIndex];
        SelectNavigatorDifference(difference);
    }

    private void SynchronizeScroll(ScrollViewer source, ScrollViewer target)
    {
        if (_synchronizingScroll)
            return;

        _synchronizingScroll = true;
        var sourceScrollableHeight = Math.Max(0, source.Extent.Height - source.Viewport.Height);
        var targetScrollableHeight = Math.Max(0, target.Extent.Height - target.Viewport.Height);
        // Line-aligned panes show the same content at the same height, so they scroll by the same amount.
        // Without alignment, the panes keep the same proportional position.
        var targetOffset = _panesAligned
            ? Math.Clamp(source.Offset.Y, 0, targetScrollableHeight)
            : targetScrollableHeight * (sourceScrollableHeight == 0 ? 0 : Math.Clamp(source.Offset.Y / sourceScrollableHeight, 0, 1));

        // Horizontal panning remains independent in each document pane.
        target.Offset = new Vector(target.Offset.X, targetOffset);
        _synchronizingScroll = false;
    }

    private void SetZoom(double zoom)
    {
        _documentZoom = Math.Clamp(Math.Round(zoom, 1), 0.7, 1.8);
        ZoomText.Text = $"{_documentZoom:P0}";
        // Pages fit their pane; zoom changes the text size and how wide a page may grow.
        ReferencePageSurface.MaxWidth = 900 * _documentZoom;
        CurrentPageSurface.MaxWidth = 900 * _documentZoom;
        RenderDocuments();
    }

    private void RenderDocuments()
    {
        if (_comparisonResult?.ReferenceDocument is null || _comparisonResult.CurrentDocument is null)
            return;

        ReferenceDocumentPanel.Children.Clear();
        CurrentDocumentPanel.Children.Clear();
        foreach (var bitmap in _documentBitmaps)
            bitmap.Dispose();
        _documentBitmaps.Clear();
        ResetAlignment();
        RenderDocument(ReferenceDocumentPanel, _comparisonResult.ReferenceDocument, isReference: true, _referenceAnchors);
        RenderDocument(CurrentDocumentPanel, _comparisonResult.CurrentDocument, isReference: false, _currentAnchors);
        ScheduleAlignment();
        ScheduleOverviewUpdate();
    }

    private void RenderDocument(
        StackPanel target,
        DocumentModel document,
        bool isReference,
        IDictionary<string, Control> anchors)
    {
        target.Children.Clear();
        anchors.Clear();
        var ranges = _comparisonResult!.Differences
            .Select(difference => new DifferenceRange(
                difference.Id,
                difference.Category,
                isReference ? difference.ReferenceRange : difference.CurrentRange))
            .Where(item => item.Range is not null)
            .ToList();
        var images = document.ImageItems.ToDictionary(image => image.Id);

        var documentReviews = DocumentReviews(isReference);
        if (documentReviews.Count > 0)
            target.Children.Add(CreateReviewBanner(documentReviews, anchors));

        // Paragraphs that exist only in the other document, keyed by the paragraph they precede here.
        var otherDocument = isReference ? _comparisonResult.CurrentDocument! : _comparisonResult.ReferenceDocument!;
        var missing = _comparisonResult.Differences
            .Where(difference => difference.CounterpartParagraphIndex is not null &&
                difference.Category == (isReference ? DifferenceCategory.ParagraphAdded : DifferenceCategory.ParagraphRemoved))
            .ToList();
        // Gaps are keyed by the block they precede here; gaps at the end of a part by that part (null = body).
        var align = CanAlign;
        // With alignment, whole tables present only in the other document also get a gap here.
        var missingTables = align
            ? _comparisonResult.Differences.Where(difference => IsOneSidedTable(difference) &&
                (isReference ? difference.ReferenceTableIndex : difference.CurrentTableIndex) is null)
            : [];
        var (missingBefore, missingAtEnd) = align
            ? PlaceMissing(missing.Concat(missingTables).ToList(), document, otherDocument, isReference)
            : (missing.Where(difference => difference.CounterpartParagraphIndex != DocumentDifference.EndOfDocument)
                    .ToLookup(difference => $"P:{difference.CounterpartParagraphIndex}"),
                missing.Where(difference => difference.CounterpartParagraphIndex == DocumentDifference.EndOfDocument)
                    .ToLookup(difference => SourcePart(difference, otherDocument, isReference)?.Key));

        // A header, footer or note that exists only in the other document is shown as an empty area here:
        // headers above the body, footers and notes below it, as the other document lays them out.
        var presentKeys = document.DisplayBlocks.OfType<PartDisplayBlock>().Select(block => block.Part.Key).ToHashSet();
        var missingParts = otherDocument.DisplayBlocks.OfType<PartDisplayBlock>().Select(block => block.Part)
            .Where(part => !presentKeys.Contains(part.Key) && missingAtEnd[part.Key].Any())
            .ToList();

        RenderBlocks(target, document.DisplayBlocks, partKey: null);
        AddMissingParts(target, missingParts.Where(part => part.Kind != DocumentPartKind.Header));

        void AddMissingParts(StackPanel panel, IEnumerable<DocumentPart> parts)
        {
            foreach (var part in parts)
            {
                var placeholder = new StackPanel { Spacing = 6 };
                foreach (var difference in missingAtEnd[part.Key])
                    placeholder.Children.Add(CreateMissingPlaceholder(difference, isReference, anchors));
                var partPanel = CreatePartPanel(part, placeholder, isReference ? "only in Current" : "missing from Current");
                panel.Children.Add(partPanel);
                // Not a fixed-height gap: the gaps inside it take the height of each missing block, so it grows
                // with them and the content below moves by exactly the space added.
                RegisterSync(isReference, $"PART:{part.Key}", partPanel);
            }
        }

        void RenderBlocks(StackPanel panel, IReadOnlyList<DocumentDisplayBlock> blocks, string? partKey)
        {
            // The top level holds headers, then the body, then footers and notes. Gaps for content missing at
            // the end of the body belong right after the body, before the footers, as in the other document.
            var bodySeen = false;
            var endFlushed = false;
            void FlushEnd()
            {
                if (endFlushed)
                    return;
                endFlushed = true;
                foreach (var difference in missingAtEnd[partKey])
                    panel.Children.Add(CreateMissingPlaceholder(difference, isReference, anchors));
            }

            foreach (var block in blocks)
            {
                if (block is not PartDisplayBlock)
                {
                    // Headers that exist only in the other document go after this document's headers.
                    if (!bodySeen && partKey is null)
                        AddMissingParts(panel, missingParts.Where(part => part.Kind == DocumentPartKind.Header));
                    bodySeen = true;
                }
                else if (bodySeen)
                    FlushEnd();
                switch (block)
                {
                    case PartDisplayBlock part:
                        var content = new StackPanel { Spacing = 8 };
                        var partPanel = CreatePartPanel(part.Part, content, null);
                        RegisterSync(isReference, $"PART:{part.Part.Key}", partPanel);
                        RenderBlocks(content, part.Blocks, part.Part.Key);
                        panel.Children.Add(partPanel);
                        break;
                    case ParagraphDisplayBlock paragraph:
                        // Without alignment, gaps keep the engine's paragraph-based position ("P:" + its index).
                        var paragraphKey = align ? ParagraphKey(paragraph.Paragraph.Index, isReference) : $"P:{paragraph.Paragraph.Index}";
                        foreach (var difference in missingBefore[paragraphKey])
                            panel.Children.Add(CreateMissingPlaceholder(difference, isReference, anchors));
                        var paragraphPanel = CreateParagraphPanel(paragraph.Paragraph, isReference, anchors, ranges, images);
                        panel.Children.Add(paragraphPanel);
                        RegisterSync(isReference, paragraphKey, paragraphPanel);
                        break;
                    case TableDisplayBlock table:
                        var tableKey = align ? TableSyncKey(table.TableIndex, isReference) : $"T:{table.TableIndex}";
                        foreach (var difference in missingBefore[tableKey])
                            panel.Children.Add(CreateMissingPlaceholder(difference, isReference, anchors));
                        // Both panes lay out a table pair with the same row list, Reference first.
                        var otherTable = align && _comparisonResult.CounterpartTableOf(table.TableIndex, isReference) is { } otherIndex
                            ? FindTable(otherDocument, otherIndex)
                            : null;
                        var rows = otherTable is null
                            ? null
                            : isReference ? AlignedRows(table, otherTable) : AlignedRows(otherTable, table);
                        var tablePanel = CreateTablePanel(table, isReference, anchors, ranges, images,
                            rows is null ? null : new TableAlignmentInfo(tableKey, rows, otherTable!));
                        panel.Children.Add(tablePanel);
                        RegisterSync(isReference, tableKey, tablePanel);
                        break;
                }
            }
            FlushEnd();
        }
    }

    /// <summary>The part (header, footer, note or null for body) that a missing paragraph comes from in the other document.</summary>
    private static DocumentPart? SourcePart(DocumentDifference difference, DocumentModel otherDocument, bool isReference)
    {
        var location = isReference ? difference.CurrentLocation : difference.ReferenceLocation;
        return otherDocument.Paragraphs.FirstOrDefault(paragraph => paragraph.Index == location?.ParagraphIndex)?.Part;
    }

    /// <summary>A labelled, dashed area for a header, footer or note, drawn like the grey band Word shows.</summary>
    private Control CreatePartPanel(DocumentPart part, Control content, string? status)
    {
        var label = new TextBlock
        {
            Text = part.Name.ToUpperInvariant() + (status is null ? string.Empty : $" · {status.ToUpperInvariant()}"),
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse(status is null ? "#64748B" : "#9D174D")),
            Margin = new Thickness(0, 0, 0, 4)
        };
        var panel = new StackPanel();
        panel.Children.Add(label);
        panel.Children.Add(content);
        var border = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
            BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6)
        };
        ToolTip.SetTip(border, part.Kind switch
        {
            DocumentPartKind.Header => "Header: repeats at the top of every page of its section.",
            DocumentPartKind.Footer => "Footer: repeats at the bottom of every page of its section.",
            DocumentPartKind.Footnote => "Footnote: shown at the bottom of the page where it is referenced.",
            _ => "Endnote: shown at the end of the document."
        });
        return border;
    }

    private Control CreateParagraphPanel(
        ParagraphBlock paragraph,
        bool isReference,
        IDictionary<string, Control> anchors,
        IReadOnlyList<DifferenceRange> ranges,
        IReadOnlyDictionary<string, DocumentImage> images)
    {
        var layout = paragraph.Layout;
        var line = new WrapPanel
        {
            HorizontalAlignment = GetParagraphAlignment(layout?.Alignment),
            Margin = ParagraphMargin(layout)
        };

        // Text removed on this side has an empty range here; mark the exact gap with a caret.
        var carets = ranges.Where(item => item.Range!.ParagraphIndex == paragraph.Index && item.Range.Length == 0 &&
                item.Category == DifferenceCategory.Text)
            .OrderBy(item => item.Range!.Start).ToList();
        void AddCarets(int upTo)
        {
            while (carets.Count > 0 && carets[0].Range!.Start <= upTo)
            {
                var caret = CreateCaret(carets[0].DifferenceId, isReference);
                line.Children.Add(caret);
                if (!anchors.ContainsKey(carets[0].DifferenceId))
                    anchors[carets[0].DifferenceId] = caret;
                carets.RemoveAt(0);
            }
        }

        foreach (var run in paragraph.Runs)
        {
            var runStart = paragraph.Runs.Take(run.Index).Sum(item => item.Text.Length);
            foreach (var fragment in SplitRun(run, paragraph.Index, runStart, ranges))
            {
                AddCarets(fragment.Start);
                var control = CreateFragment(fragment, run.Formatting, isReference, layout);
                line.Children.Add(control);
                if (fragment.DifferenceId is not null && !anchors.ContainsKey(fragment.DifferenceId))
                    anchors[fragment.DifferenceId] = control;
                foreach (var range in ranges.Where(item => item.Range?.ParagraphIndex == paragraph.Index &&
                             item.Range.Start < runStart + run.Text.Length && item.Range.Start + item.Range.Length > fragment.Start &&
                             item.Range.Start < fragment.Start + fragment.Text.Length))
                    if (!anchors.ContainsKey(range.DifferenceId)) anchors[range.DifferenceId] = control;
            }

            foreach (var imageId in run.ImageReferences)
            {
                if (!images.TryGetValue(imageId, out var image))
                    continue;

                    var control = CreateImageFragment(image, isReference, anchors);
                    line.Children.Add(control);
            }
        }
        AddCarets(int.MaxValue);

        if (line.Children.Count == 0)
            line.Children.Add(new TextBlock { Text = "(empty paragraph)", Foreground = Brushes.Gray });
        var lineDifferences = _comparisonResult!.Differences.Where(difference =>
            difference.Category != DifferenceCategory.Table && difference.Category != DifferenceCategory.Image &&
            (isReference ? difference.ReferenceLocation : difference.CurrentLocation)?.ParagraphIndex == paragraph.Index).ToList();
        if (lineDifferences.Count > 0)
        {
            line.Background = Brushes.Transparent;
            line.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(line).Properties.IsLeftButtonPressed)
                    return;
                args.Handled = true;
                SelectViewerScope(lineDifferences, "Changes in this paragraph / table cell");
            };
        }
        else if (!HasAnyFinding(paragraph.Index, isReference))
        {
            // Unchanged content is clickable too: it shows the matching content in the other document.
            line.Background = Brushes.Transparent;
            line.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(line).Properties.IsLeftButtonPressed)
                    return;
                args.Handled = true;
                SelectMatch(paragraph.Index, isReference);
            };
        }
        Control result = CreateAlignmentMarker(line, lineDifferences, anchors);

        // A whole added/removed paragraph gets a side bar, so even an empty paragraph is visible.
        var wholeParagraph = lineDifferences
            .Where(difference => difference.Category is DifferenceCategory.ParagraphAdded or DifferenceCategory.ParagraphRemoved)
            .ToList();
        if (wholeParagraph.Count > 0)
            result = CreateSideBar(result, wholeParagraph, anchors,
                isReference ? MarkerStyle.Removed : MarkerStyle.Added,
                isReference ? "This Reference paragraph is missing from Current." : "This paragraph is not in the approved Reference.");

        var reviews = lineDifferences
            .Where(difference => difference.Category == DifferenceCategory.Review &&
                (isReference ? difference.ReferenceTableIndex : difference.CurrentTableIndex) is null)
            .ToList();
        if (reviews.Count > 0)
            result = CreateSideBar(result, reviews, anchors, MarkerStyle.Review, "Needs review: " + reviews[0].ReviewNote);

        if (_selectedMatch is { } match && (isReference ? match.ReferenceIndex : match.CurrentIndex) == paragraph.Index)
        {
            result = new Border
            {
                Child = result,
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.Parse("#2563EB")),
                Background = new SolidColorBrush(Color.Parse("#EFF6FF")),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2)
            };
            anchors[MatchAnchor] = result;
        }
        return result;
    }

    private Control CreateAlignmentMarker(Control line, IReadOnlyList<DocumentDifference> lineDifferences, IDictionary<string, Control> anchors)
    {
        var alignmentDifference = lineDifferences.FirstOrDefault(difference => difference.Property == "Paragraph alignment");
        if (alignmentDifference is null)
            return line;

        var alignmentMarker = new Border
        {
            Child = line,
            Padding = new Thickness(0, 0, 0, 2),
            BorderThickness = new Thickness(0, 0, 0, 3),
            BorderBrush = _showChangeMarkers ? new SolidColorBrush(Color.Parse("#DB2777")) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(alignmentMarker, $"Alignment differs: Reference {alignmentDifference.ReferenceValue}; Current {alignmentDifference.CurrentValue}");
        alignmentMarker.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(alignmentMarker).Properties.IsLeftButtonPressed)
                return;
            args.Handled = true;
            SelectFromViewer(alignmentDifference);
        };
        if (!anchors.ContainsKey(alignmentDifference.Id))
            anchors[alignmentDifference.Id] = alignmentMarker;
        return alignmentMarker;
    }

    private enum MarkerStyle { Removed, Added, Missing, Review }

    /// <summary>Bar color, active bar color and background for each kind of block-level marker.</summary>
    private static (string Bar, string Active, string Background) MarkerColors(MarkerStyle style) => style switch
    {
        MarkerStyle.Removed => ("#F87171", "#BE123C", "#FFF1F2"),
        MarkerStyle.Added => ("#4ADE80", "#166534", "#F0FDF4"),
        MarkerStyle.Missing => ("#F472B6", "#9D174D", "#FDF2F8"),
        _ => ("#F59E0B", "#B45309", "#FFFBEB")
    };

    /// <summary>Wraps a block with a colored left bar; clicking it focuses the given findings.</summary>
    private Control CreateSideBar(Control child, IReadOnlyList<DocumentDifference> differences, IDictionary<string, Control> anchors,
        MarkerStyle style, string tip)
    {
        var colors = MarkerColors(style);
        var active = differences.Any(difference => difference.Id == _selectedDifferenceId);
        var bar = new Border
        {
            Child = child,
            Padding = new Thickness(6, 2, 2, 2),
            BorderThickness = new Thickness(active ? 6 : 4, 0, 0, 0),
            BorderBrush = _showChangeMarkers ? new SolidColorBrush(Color.Parse(active ? colors.Active : colors.Bar)) : Brushes.Transparent,
            Background = _showChangeMarkers ? new SolidColorBrush(Color.Parse(colors.Background)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(bar, tip);
        bar.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(bar).Properties.IsLeftButtonPressed)
                return;
            args.Handled = true;
            SelectViewerScope(differences, differences.Count == 1 ? "Selected value · showing only this change" : "Changes in this paragraph / table cell");
        };
        foreach (var difference in differences)
            if (!anchors.ContainsKey(difference.Id))
                anchors[difference.Id] = bar;
        return bar;
    }

    /// <summary>A thin mark between two characters where text exists only on the other side.</summary>
    private Control CreateCaret(string differenceId, bool isReference)
    {
        var difference = _comparisonResult!.Differences.First(item => item.Id == differenceId);
        var colors = MarkerColors(MarkerStyle.Missing);
        var caret = new Border
        {
            Width = difference.Id == _selectedDifferenceId ? 4 : 3,
            Height = 16 * _documentZoom,
            Margin = new Thickness(1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = _showChangeMarkers ? new SolidColorBrush(Color.Parse(difference.Id == _selectedDifferenceId ? colors.Active : colors.Bar)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var missing = FormatDetailValue(isReference ? difference.CurrentValue : difference.ReferenceValue);
        ToolTip.SetTip(caret, isReference ? $"Current adds here: {missing}" : $"Missing here: {missing}");
        caret.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(caret).Properties.IsLeftButtonPressed)
                return;
            args.Handled = true;
            SelectFromViewer(difference);
        };
        return caret;
    }

    /// <summary>Shows where a paragraph that exists only in the other document would be.</summary>
    private Control CreateMissingPlaceholder(DocumentDifference difference, bool isReference, IDictionary<string, Control> anchors)
    {
        var value = isReference ? difference.CurrentValue : difference.ReferenceValue;
        var text = string.IsNullOrWhiteSpace(value) ? "an empty paragraph" : $"“{ShortLabel(value)}”";
        var isTable = IsOneSidedTable(difference);
        var label = new TextBlock
        {
            Text = isTable
                ? isReference ? $"Table added in Current · {value}" : $"Table missing here · {value}"
                : isReference ? $"Added in Current: {text}" : $"Missing here: {text}",
            FontStyle = FontStyle.Italic,
            FontSize = 12 * _documentZoom,
            Foreground = new SolidColorBrush(Color.Parse("#9D174D")),
            TextWrapping = TextWrapping.Wrap
        };
        var item = isTable ? "table" : "paragraph";
        var placeholder = CreateSideBar(label, [difference], anchors, MarkerStyle.Missing,
            isReference ? $"Current has a {item} here that the Reference does not." : $"The Reference has a {item} here that is missing from Current.");
        // The gap grows to the height of the missing paragraph, so the following lines stay level.
        RegisterSync(isReference, MissingKey(difference), placeholder, isPlaceholder: true);
        return placeholder;
    }

    /// <summary>Lists document-wide review items (headers, footers, notes) at the top of the page.</summary>
    private Control CreateReviewBanner(IReadOnlyList<DocumentDifference> reviews, IDictionary<string, Control> anchors)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = "NEEDS REVIEW · not compared by this tool", FontWeight = FontWeight.Bold, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#92400E")) });
        foreach (var review in reviews)
            panel.Children.Add(new TextBlock { Text = review.ReviewNote, TextWrapping = TextWrapping.Wrap, FontSize = 12 * _documentZoom });
        return CreateSideBar(panel, reviews, anchors, MarkerStyle.Review, "Select to see the review findings for this document.");
    }

    /// <summary>Review findings that belong to this side's document as a whole rather than one location.</summary>
    private IReadOnlyList<DocumentDifference> DocumentReviews(bool isReference) => _comparisonResult!.Differences
        .Where(difference => difference.Category == DifferenceCategory.Review &&
            difference.ReferenceLocation is null && difference.CurrentLocation is null &&
            IsReferenceSideReview(difference) == isReference)
        .ToList();

    // A review finding is created for the side whose content needs checking; the other side reads "(not checked here)".
    private static bool IsReferenceSideReview(DocumentDifference difference) =>
        difference.CurrentValue == "(not checked here)";

    private Control CreateTablePanel(
        TableDisplayBlock table,
        bool isReference,
        IDictionary<string, Control> anchors,
        IReadOnlyList<DifferenceRange> ranges,
        IReadOnlyDictionary<string, DocumentImage> images,
        TableAlignmentInfo? alignment = null)
    {
        var content = CreateTableContent(table, isReference, anchors, ranges, images, alignment);
        var reviews = _comparisonResult!.Differences
            .Where(difference => difference.Category == DifferenceCategory.Review &&
                (isReference ? difference.ReferenceTableIndex : difference.CurrentTableIndex) == table.TableIndex)
            .ToList();
        return reviews.Count == 0
            ? content
            : CreateSideBar(content, reviews, anchors, MarkerStyle.Review, "Needs review: " + reviews[0].ReviewNote);
    }

    /// <summary>A cell's first grid column; cells built without grid positions use their place in the row.</summary>
    private static int GridColumnOf(DocumentTableCell cell, int indexInRow) => FindingText.GridColumnOf(cell, indexInRow);

    private Control CreateTableContent(
        TableDisplayBlock table,
        bool isReference,
        IDictionary<string, Control> anchors,
        IReadOnlyList<DifferenceRange> ranges,
        IReadOnlyDictionary<string, DocumentImage> images,
        TableAlignmentInfo? alignment = null)
    {
        // The table grid is as wide as its widest row, counting merged cells by the columns they cover.
        var columnCount = Math.Max(1, table.Rows
            .Select(row => row.Cells.Select((cell, index) => GridColumnOf(cell, index) + cell.ColumnSpan).DefaultIfEmpty(0).Max())
            .DefaultIfEmpty(0).Max());
        var grid = new Grid
        {
            Margin = new Thickness(0, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        for (var column = 0; column < columnCount; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        // Without a paired table, the rows are drawn as they are; with one, both tables share one row list.
        var visualRows = alignment?.Rows.Select(row => isReference ? row.Reference : row.Current).ToList()
            ?? Enumerable.Range(0, table.Rows.Count).Select(row => (int?)row).ToList();
        if (alignment is not null)
            (isReference ? _referenceTableGrids : _currentTableGrids)[alignment.Key] = grid;
        // Where each of this table's rows is drawn; placeholder rows can sit between them.
        var gridRowOf = visualRows.Select((row, gridRow) => (row, gridRow)).Where(item => item.row is not null)
            .ToDictionary(item => item.row!.Value, item => item.gridRow);

        for (var gridRow = 0; gridRow < visualRows.Count; gridRow++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (visualRows[gridRow] is not { } rowIndex)
            {
                var otherRow = isReference ? alignment!.Rows[gridRow].Current!.Value : alignment!.Rows[gridRow].Reference!.Value;
                var missingRow = CreateMissingRow(alignment.Other, otherRow, isReference, anchors);
                Grid.SetRow(missingRow, gridRow);
                Grid.SetColumnSpan(missingRow, columnCount);
                grid.Children.Add(missingRow);
                continue;
            }
            var row = table.Rows[rowIndex];
            for (var columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
            {
                var tableCell = row.Cells[columnIndex];
                // A cell merged into the one above is covered by that cell, as in Word.
                if (tableCell.MergedFromAbove)
                    continue;
                var content = new StackPanel { Spacing = 3 };
                foreach (var paragraph in tableCell.Paragraphs)
                    content.Children.Add(CreateParagraphPanel(paragraph, isReference, anchors, ranges, images));

                var cell = new Border
                {
                    Background = rowIndex == 0 ? new SolidColorBrush(Color.Parse("#F8FAFC")) : Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(6),
                    Child = content
                };
                Grid.SetRow(cell, gridRow);
                Grid.SetColumn(cell, GridColumnOf(tableCell, columnIndex));
                Grid.SetColumnSpan(cell, tableCell.ColumnSpan);
                if (tableCell.RowSpan > 1)
                {
                    // The last covered row may be drawn lower when placeholder rows sit in between.
                    var lastRow = Math.Min(rowIndex + tableCell.RowSpan - 1, table.Rows.Count - 1);
                    Grid.SetRowSpan(cell, gridRowOf.GetValueOrDefault(lastRow, gridRow) - gridRow + 1);
                }
                grid.Children.Add(cell);
            }
        }

        var structuralDifference = FindTableDifference(table.TableIndex, isReference);
        var missingFromCurrent = isReference ? new List<DocumentDifference>() : MissingTableDifferences(table.TableIndex).ToList();
        var primaryDifference = missingFromCurrent.FirstOrDefault() ?? structuralDifference;
        if (primaryDifference is null)
            return grid;

        var active = primaryDifference.Id == _selectedDifferenceId;
        var representationChange = IsImageTableRepresentation(primaryDifference);
        var pinkMissingStructure = missingFromCurrent.Count > 0;
        var border = new Border
        {
            Padding = new Thickness(3),
            BorderThickness = new Thickness(active ? 3 : 2),
            Background = _showChangeMarkers && (pinkMissingStructure || representationChange)
                ? new SolidColorBrush(Color.Parse(pinkMissingStructure ? "#FDF2F8" : "#F1F5F9")) : Brushes.Transparent,
            BorderBrush = !_showChangeMarkers ? Brushes.Transparent : pinkMissingStructure
                ? new SolidColorBrush(Color.Parse(active ? "#9D174D" : "#DB2777"))
                : representationChange ? new SolidColorBrush(Color.Parse(active ? "#334155" : "#64748B"))
                : new SolidColorBrush(Color.Parse(isReference ? active ? "#E11D48" : "#FCA5A5" : active ? "#15803D" : "#86EFAC")),
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (pinkMissingStructure)
            ToolTip.SetTip(border, "Reference table content is missing from Current. Select to see the exact row or column in Change Tracker.");
        else if (representationChange)
            ToolTip.SetTip(border, "Representation changed: Reference and Current use an image and a table. Select to review the mapped values.");
        border.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
                return;
            args.Handled = true;
            SelectFromViewer(primaryDifference);
        };
        if (!anchors.ContainsKey(primaryDifference.Id))
            anchors[primaryDifference.Id] = border;
        foreach (var difference in missingFromCurrent)
            if (!anchors.ContainsKey(difference.Id))
                anchors[difference.Id] = border;
        return border;
    }

    private Control CreateFragment(DisplayFragment fragment, RunFormatting formatting, bool isReference, ParagraphLayout? layout)
    {
        // Long text wraps at word boundaries inside the pane instead of running past its edge or splitting words.
        var text = new TextBlock { TextWrapping = TextWrapping.WrapWithOverflow };
        var inline = new Run(fragment.Text);
        text.Inlines!.Add(inline);
        ApplyFormatting(text, formatting);
        ApplyParagraphStyle(text, layout);

        if (fragment.DifferenceId is null)
            return text;

        var selected = _comparisonResult?.Differences.FirstOrDefault(item => item.Id == fragment.DifferenceId);
        var active = string.Equals(fragment.DifferenceId, _selectedDifferenceId, StringComparison.Ordinal);
        var pinkSquare = selected?.Category == DifferenceCategory.Formatting ||
            isReference && selected is not null && IsMissingFromCurrent(selected);
        var marker = new Border
        {
            Child = text,
            Background = _showChangeMarkers && pinkSquare ? new SolidColorBrush(Color.Parse("#FDF2F8")) : Brushes.Transparent,
            Padding = pinkSquare ? new Thickness(2, 1) : new Thickness(0, 0, 0, 2),
            BorderThickness = pinkSquare ? new Thickness(active ? 2 : 1) : new Thickness(0, 0, 0, 2),
            BorderBrush = !_showChangeMarkers ? Brushes.Transparent : new SolidColorBrush(Color.Parse(pinkSquare
                ? active ? "#9D174D" : "#F472B6"
                : isReference ? active ? "#BE123C" : "#F87171" : active ? "#166534" : "#4ADE80")),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (selected is not null)
            ToolTip.SetTip(marker, $"Reference: {FormatDetailValue(selected.ReferenceValue)}\nCurrent: {FormatDetailValue(selected.CurrentValue)}");
        marker.PointerPressed += (_, eventArgs) =>
        {
            if (!eventArgs.GetCurrentPoint(marker).Properties.IsLeftButtonPressed)
                return;
            eventArgs.Handled = true;
            var difference = _comparisonResult?.Differences.FirstOrDefault(item => item.Id == fragment.DifferenceId);
            if (difference is not null)
            {
                var matches = _comparisonResult!.Differences.Where(item =>
                {
                    var range = isReference ? item.ReferenceRange : item.CurrentRange;
                    return range is not null && range.ParagraphIndex == fragment.ParagraphIndex &&
                        range.Start < fragment.Start + fragment.Text.Length && range.Start + range.Length > fragment.Start;
                }).ToList();
                SelectViewerScope(matches, "Changes affecting this selected value");
            }
        };
        return marker;
    }

    private Control CreateImageFragment(DocumentImage image, bool isReference, IDictionary<string, Control> anchors)
    {
        var imageDifferences = FindImageDifferences(image.Id, isReference).ToList();
        var difference = imageDifferences.FirstOrDefault();
        var active = imageDifferences.Any(item => item.Id == _selectedDifferenceId);
        var representationDifference = imageDifferences.FirstOrDefault(IsImageTableRepresentation);
        // Image/table swaps are grey; other image changes use the side colour (red Reference, green Current);
        // unchanged images keep a thin neutral frame.
        var (frame, activeFrame, fill) = representationDifference is not null ? ("#64748B", "#334155", "#F1F5F9")
            : difference is null ? ("#CBD5E1", "#CBD5E1", (string?)null)
            : isReference ? ("#F87171", "#BE123C", "#FFF1F2") : ("#4ADE80", "#166534", "#F0FDF4");
        var border = new Border
        {
            Margin = new Thickness(4, 2),
            Padding = new Thickness(4),
            BorderThickness = new Thickness(difference is null ? 1 : active ? 3 : 2),
            Background = fill is not null && _showChangeMarkers ? new SolidColorBrush(Color.Parse(fill)) : Brushes.Transparent,
            BorderBrush = !_showChangeMarkers ? Brushes.Transparent : new SolidColorBrush(Color.Parse(active ? activeFrame : frame))
        };
        if (representationDifference is not null)
            ToolTip.SetTip(border, "Representation changed: Reference and Current use an image and a table. Select to review the mapped values.");

        try
        {
            using var stream = new MemoryStream(image.Data);
            var bitmap = new Bitmap(stream);
            _documentBitmaps.Add(bitmap);
            var scale = Math.Min(_documentZoom, Math.Min(380d * _documentZoom / bitmap.PixelSize.Width, 280d * _documentZoom / bitmap.PixelSize.Height));
            var imageWidth = bitmap.PixelSize.Width * scale;
            var imageHeight = bitmap.PixelSize.Height * scale;
            var canvas = new Canvas { Width = imageWidth, Height = imageHeight + 8, ClipToBounds = true };
            canvas.Children.Add(new Image
            {
                Source = bitmap,
                Width = imageWidth,
                Height = imageHeight,
                Stretch = Stretch.Uniform
            });
            foreach (var regionDifference in imageDifferences.Where(item => item.RegionFor(isReference) is not null)
                         .OrderByDescending(item => item.RegionFor(isReference)!.Width * item.RegionFor(isReference)!.Height))
            {
                var region = regionDifference.RegionFor(isReference)!;
                var overlay = new Border
                {
                    Width = Math.Max(8, region.Width * scale),
                    Height = Math.Max(8, region.Height * scale),
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                Canvas.SetLeft(overlay, region.X * scale);
                Canvas.SetTop(overlay, region.Y * scale);
                ToolTip.SetTip(overlay, $"{FormatDetailValue(regionDifference.ReferenceValue)} → {FormatDetailValue(regionDifference.CurrentValue)}");
                overlay.PointerPressed += (_, eventArgs) =>
                {
                    if (!eventArgs.GetCurrentPoint(overlay).Properties.IsLeftButtonPressed)
                        return;
                    eventArgs.Handled = true;
                    SelectFromViewer(regionDifference);
                };
                canvas.Children.Add(overlay);
                // The hit target is transparent. Only an underline outside the changed
                // pixels is drawn, so even very small digits remain unobscured.
                var underline = new Border
                {
                    Width = Math.Max(8, Math.Min(region.Width * scale, imageWidth - region.X * scale)),
                    Height = regionDifference.Id == _selectedDifferenceId ? 2 : 1,
                    Background = new SolidColorBrush(Color.Parse(isReference ? "#BE123C" : "#15803D")),
                    IsHitTestVisible = false,
                    IsVisible = _showChangeMarkers
                };
                Canvas.SetLeft(underline, region.X * scale);
                Canvas.SetTop(underline, Math.Min(imageHeight, (region.Y + region.Height) * scale) + 2);
                canvas.Children.Add(underline);
                if (!anchors.ContainsKey(regionDifference.Id))
                    anchors[regionDifference.Id] = overlay;
            }
            border.Child = canvas;
        }
        catch
        {
            border.Child = new TextBlock
            {
                Text = $"Image preview unavailable ({image.ContentType})",
                Foreground = Brushes.Gray
            };
        }

        if (difference is not null)
        {
            border.Cursor = new Cursor(StandardCursorType.Hand);
            border.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
                    return;
                args.Handled = true;
                SelectViewerScope(imageDifferences, "Changes in this image · select a value below");
            };
            foreach (var imageDifference in imageDifferences)
                if (!anchors.ContainsKey(imageDifference.Id))
                    anchors[imageDifference.Id] = border;
        }

        return border;
    }

    private static bool IsMissingFromCurrent(DocumentDifference difference) =>
        GetDisplayContentKind(difference, isReference: false) == DifferenceContentKind.Missing;

    private IEnumerable<DocumentDifference> MissingTableDifferences(int tableIndex) =>
        _comparisonResult!.Differences.Where(difference =>
            IsMissingFromCurrent(difference) && difference.ReferenceLocation?.TableCell?.TableIndex == tableIndex);

    private DocumentDifference? FindImageDifference(string imageId, bool isReference) =>
        FindImageDifferences(imageId, isReference).FirstOrDefault();

    private IEnumerable<DocumentDifference> FindImageDifferences(string imageId, bool isReference) =>
        _comparisonResult?.Differences.Where(difference => isReference
            ? difference.ReferenceImageId == imageId
            : difference.CurrentImageId == imageId) ?? [];

    private DocumentDifference? FindTableDifference(int tableIndex, bool isReference) =>
        _comparisonResult?.Differences.FirstOrDefault(difference =>
            (difference.Category == DifferenceCategory.Table || IsImageTableRepresentation(difference)) &&
            (isReference ? difference.ReferenceTableIndex == tableIndex : difference.CurrentTableIndex == tableIndex));

    private static bool IsImageTableRepresentation(DocumentDifference difference)
    {
        var reference = GetDisplayContentKind(difference, isReference: true);
        var current = GetDisplayContentKind(difference, isReference: false);
        return reference == DifferenceContentKind.Image && current == DifferenceContentKind.Table ||
            reference == DifferenceContentKind.Table && current == DifferenceContentKind.Image;
    }

    private void SelectFromViewer(DocumentDifference difference)
    {
        SelectViewerScope([difference], "Selected value · showing only this change");
    }

    private void SelectViewerScope(IReadOnlyList<DocumentDifference> differences, string label)
    {
        if (differences.Count == 0) return;
            var ids = differences.Select(difference => difference.Id).ToHashSet();
            ShowNavigatorItems(_navigatorItems.Where(item => ids.Contains(item.Difference.Id)).ToList(), label);
            SelectNavigatorDifference(differences.FirstOrDefault(difference => difference.Id == _selectedDifferenceId) ?? differences[0]);
    }

    private void ShowNavigatorItems(IReadOnlyList<NavigatorItem> items, string? scope)
    {
        DifferenceList.ItemsSource = items;
        DifferenceCountText.Text = scope is null ? $"{items.Count} total" : $"{items.Count} of {_navigatorItems.Count}";
        SelectionScopeText.Text = scope ?? "All changes · click a highlighted value to focus";
        ShowAllChangesButton.IsVisible = scope is not null;
    }

    private void ShowAllChanges_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FindingFilter.SelectedIndex = 0;
        ShowNavigatorItems(_navigatorItems, null);
        if (_selectedDifferenceId is { } id && _comparisonResult?.Differences.FirstOrDefault(item => item.Id == id) is { } difference)
            SelectNavigatorDifference(difference);
    }

    private void FindingFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DifferenceList is null || FindingFilter is null) return;
        var index = FindingFilter.SelectedIndex;
        var items = _navigatorItems.Where(item => index switch
        {
            1 => item.Difference.Category is DifferenceCategory.ParagraphAdded or DifferenceCategory.ParagraphRemoved ||
                 item.Difference.ReferenceValue == "(no corresponding content)" || item.Difference.CurrentValue == "(no corresponding content)",
            2 => item.Difference.Category == DifferenceCategory.Table || item.Difference.ReferenceLocation?.TableCell is not null || item.Difference.CurrentLocation?.TableCell is not null,
            3 => item.Difference.Category == DifferenceCategory.Image,
            4 => item.Difference.ReviewNote is not null || item.Difference.Category == DifferenceCategory.Review,
            _ => true
        }).ToList();
        ShowNavigatorItems(items, index == 0 ? null : "Filtered findings");
    }

    private void SelectNavigatorDifference(DocumentDifference difference)
    {
        var item = FindNavigatorItem(difference);
        if (item is not null && DifferenceList.ItemsSource is IReadOnlyList<NavigatorItem> visible && !visible.Contains(item))
            ShowNavigatorItems(_navigatorItems, null);
        if (item is not null && !Equals(DifferenceList.SelectedItem, item))
        {
            DifferenceList.SelectedItem = item;
            return;
        }

        // SelectionChanged is not raised if the item is already selected.
        SelectDifference(difference);
    }

    private NavigatorItem? FindNavigatorItem(DocumentDifference difference) =>
        _navigatorItems.FirstOrDefault(item => item.Difference.Id == difference.Id);

    private IReadOnlyList<NavigatorItem> BuildNavigatorItems(IReadOnlyList<DocumentDifference> differences)
    {
        var lineCounts = differences
            .GroupBy(GetLineKey)
            .ToDictionary(group => group.Key, group => group.Count());

        return differences.Select(difference =>
        {
            var lineKey = GetLineKey(difference);
            var location = difference.ReferenceLocation ?? difference.CurrentLocation;
            var locationText = location?.Display ?? "No corresponding document line";
            var operation = GetOperation(difference);
            var count = lineCounts[lineKey];
            return new NavigatorItem(
                difference,
                operation,
                DescribeLocation(difference),
                count,
                BuildTransitionAndCount(difference, count),
                BuildNavigatorSummary(difference),
                $"Reference: {DetailValue(difference, true)}",
                $"Current: {DetailValue(difference, false)}");
        }).ToList();
    }

    private static string GetLineKey(DocumentDifference difference)
    {
        var location = difference.ReferenceLocation ?? difference.CurrentLocation;
        return location is null
            ? $"unlocated:{difference.Id}"
            : $"paragraph:{location.ParagraphIndex}";
    }

    private static string GetOperation(DocumentDifference difference) => FindingText.Operation(difference);

    private static string BuildNavigatorSummary(DocumentDifference difference)
    {
        var reference = FormatDetailValue(difference.ReferenceValue);
        var current = FormatDetailValue(difference.CurrentValue);
        return $"{reference} -> {current}";
    }

    private string BuildSourceTruthExplanation(DocumentDifference difference)
    {
        if (IsSpaceChange(difference))
            return difference.CurrentValue == "(no corresponding content)"
                ? $"Current is missing {WhitespaceName(difference.ReferenceValue)}. Compare the text below."
                : difference.ReferenceValue == "(no corresponding content)"
                    ? $"Current has extra {WhitespaceName(difference.CurrentValue)}. Compare the text below."
                    : "The spacing between these characters changed. Compare the text below.";
        if (difference.Category == DifferenceCategory.Review)
            return "This part could not be fully compared. Please check it in the original documents.";
        if (difference.IsRecognizedText)
            return $"Image recognition read '{ShortDetailValue(difference.ReferenceValue)}' in Reference and '{ShortDetailValue(difference.CurrentValue)}' in Current. Check the image crop to confirm the difference.";
        if (difference.Category == DifferenceCategory.Formatting && difference.Property is "Subscript" or "Superscript")
        {
            var range = difference.ReferenceRange;
            var paragraph = _comparisonResult?.ReferenceDocument?.Paragraphs.FirstOrDefault(p => p.Index == range?.ParagraphIndex);
            var value = paragraph is not null && range is not null ? paragraph.Text.Substring(range.Start, range.Length) : "selected text";
            var position = difference.Property == "Subscript" ? "below" : "above";
            return difference.ReferenceValue == "On"
                ? $"'{value}' should appear {position} the text line, as in Reference. This formatting is missing in Current."
                : $"'{value}' appears {position} the text line in Current. Reference does not use this formatting.";
        }
        var referenceKindValue = GetDisplayContentKind(difference, isReference: true);
        var currentKindValue = GetDisplayContentKind(difference, isReference: false);
        var referenceKind = ContentKindLabel(referenceKindValue).ToLowerInvariant();
        var currentKind = ContentKindLabel(currentKindValue).ToLowerInvariant();
        var reference = ShortDetailValue(FormatDetailValue(difference.ReferenceValue));
        var current = ShortDetailValue(FormatDetailValue(difference.CurrentValue));
        if (referenceKindValue == DifferenceContentKind.Missing)
            return $"Current contains extra {currentKind} content “{current}” that is not in the approved Reference.";
        if (currentKindValue == DifferenceContentKind.Missing)
            return $"Missing from Current: the approved Reference {referenceKind} value is “{reference}”.";
        if (difference.Property == "Image / table representation")
            return $"Reference uses {referenceKind}; Current uses {currentKind}. The document structure has changed.";
        if (difference.Property == "Paragraph alignment")
            return $"Alignment is wrong: Reference requires {reference} alignment; Current uses {current} alignment.";
        if (difference.Property == "Hidden")
            return difference.CurrentValue == "On"
                ? "This text is visible in the approved document but hidden in Current, so it will not appear when Current is printed or read."
                : "This text is hidden in the approved document but visible in Current.";
        if (difference.Category == DifferenceCategory.Formatting)
            return $"{difference.Property} is “{reference}” in the approved document but “{current}” in Current. The text itself is the same.";
        if (string.Equals(difference.ReferenceValue, difference.CurrentValue, StringComparison.Ordinal))
            return $"The {difference.Property.ToLowerInvariant()} differs: Reference requires “{reference}”; Current uses “{current}”.";
        return $"Reference requires “{reference}”; Current contains “{current}”.";
    }

    private static string ShortDetailValue(string value) => value.Length <= 120 ? value : value[..117] + "…";

    private static bool IsSpaceChange(DocumentDifference d) => FindingText.IsSpaceChange(d);

    private static string WhitespaceName(string value) => value == " " ? "a space" :
        value.All(c => c == ' ') ? $"{value.Length} spaces" : value == "\t" ? "a tab" : "spacing";

    private string DetailValue(DocumentDifference difference, bool reference)
    {
        if (IsSpaceChange(difference))
        {
            var range = reference ? difference.ReferenceRange : difference.CurrentRange;
            var doc = reference ? _comparisonResult?.ReferenceDocument : _comparisonResult?.CurrentDocument;
            var p = doc?.Paragraphs.FirstOrDefault(p => p.Index == range?.ParagraphIndex);
            if (p is not null && range is not null)
            {
                var start = Math.Max(0, range.Start - 35);
                var end = Math.Min(p.Text.Length, range.Start + range.Length + 35);
                return (start > 0 ? "…" : "") + p.Text[start..end] + (end < p.Text.Length ? "…" : "");
            }
        }
        return FormatDetailValue(reference ? difference.ReferenceValue : difference.CurrentValue);
    }

    private static DifferenceContentKind GetStructureKind(DocumentDifference difference, bool reference)
    {
        var kind = GetDisplayContentKind(difference, reference);
        var location = reference ? difference.ReferenceLocation : difference.CurrentLocation;
        return kind == DifferenceContentKind.Missing && location is not null
            ? location.TableCell is not null ? DifferenceContentKind.Table : DifferenceContentKind.Text : kind;
    }

    private static string BuildUserAction(DocumentDifference difference)
    {
        var reference = ShortDetailValue(FormatDetailValue(difference.ReferenceValue));
        if (RequiresReview(difference))
            return "Recommended action: inspect the original evidence before changing Current.";
        if (GetDisplayContentKind(difference, isReference: false) == DifferenceContentKind.Missing)
            return $"Recommended action: restore the approved Reference value '{reference}' in Current.";
        if (GetDisplayContentKind(difference, isReference: true) == DifferenceContentKind.Missing)
            return "Recommended action: remove the extra Current content or approve it as an intentional change.";
        if (difference.Property == "Paragraph alignment")
            return $"Recommended action: set Current alignment to {reference}.";
        if (difference.Property == "Hidden")
            return difference.CurrentValue == "On"
                ? "Recommended action: unhide this text in Current (in Word: Font, clear \"Hidden\"), or confirm the removal was intended."
                : "Recommended action: hide this text in Current again, or confirm it should now be visible.";
        if (difference.Category == DifferenceCategory.Formatting)
            return $"Recommended action: change Current {difference.Property.ToLowerInvariant()} to match Reference ({reference}).";
        if (RequiresReview(difference))
            return "Recommended action: inspect the highlighted original evidence before accepting this result.";
        return $"Recommended action: update Current to match the approved Reference value '{reference}'.";
    }

    private static bool RequiresReview(DocumentDifference difference) =>
        difference.Category == DifferenceCategory.Review || difference.IsRecognizedText || !string.IsNullOrWhiteSpace(difference.ReviewNote);

    private static string BuildTransitionAndCount(DocumentDifference difference, int count)
    {
        var countText = count == 1 ? "1 change here" : $"{count} changes here";
        var referenceKind = GetStructureKind(difference, true);
        var currentKind = GetStructureKind(difference, false);
        return referenceKind == currentKind
            ? countText
            : $"{ContentKindLabel(referenceKind)} → {ContentKindLabel(currentKind)} · {countText}";
    }

    private static DifferenceContentKind GetDisplayContentKind(DocumentDifference difference, bool isReference)
    {
        var declared = isReference ? difference.ReferenceContentKind : difference.CurrentContentKind;
        var value = isReference ? difference.ReferenceValue : difference.CurrentValue;
        var location = isReference ? difference.ReferenceLocation : difference.CurrentLocation;
        var imageId = isReference ? difference.ReferenceImageId : difference.CurrentImageId;
        var tableIndex = isReference ? difference.ReferenceTableIndex : difference.CurrentTableIndex;
        if (value.StartsWith("(no corresponding", StringComparison.Ordinal))
            return DifferenceContentKind.Missing;
        if (imageId is not null)
            return DifferenceContentKind.Image;
        if (tableIndex is not null || location?.TableCell is not null)
            return DifferenceContentKind.Table;
        return declared;
    }

    private static string ContentKindLabel(DifferenceContentKind kind) => kind switch
    {
        DifferenceContentKind.Image => "Image",
        DifferenceContentKind.Table => "Table",
        DifferenceContentKind.Missing => "Missing",
        _ => "Text"
    };

    private static string FormatDetailValue(string value) => value switch
    {
        "(no corresponding content)" or "(no corresponding table)" or "(no corresponding image)" => "Not present",
        "" => "(empty)",
        _ when string.IsNullOrWhiteSpace(value) => value.Replace(" ", "␠").Replace("\t", "[tab]").Replace("\n", "[line break]"),
        _ => value
    };

    private static HorizontalAlignment GetParagraphAlignment(string? alignment) =>
        string.Equals(alignment, "center", StringComparison.OrdinalIgnoreCase)
            ? HorizontalAlignment.Center
            : string.Equals(alignment, "right", StringComparison.OrdinalIgnoreCase)
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;

    private static Thickness ParagraphMargin(ParagraphLayout? layout)
    {
        var left = Math.Max(0, layout?.LeftIndentTwips ?? 0) / 15d;
        var firstLine = Math.Max(0, layout?.FirstLineIndentTwips ?? 0) / 15d;
        var top = Math.Max(0, layout?.SpaceBeforeTwips ?? 0) / 15d;
        var bottom = Math.Max(2, Math.Max(0, layout?.SpaceAfterTwips ?? 0) / 15d);
        if (layout?.PageBreakBefore == true)
            top = Math.Max(top, 30);
        return new Thickness(left + firstLine, top, 0, bottom);
    }

    private void ApplyParagraphStyle(TextBlock text, ParagraphLayout? layout)
    {
        var style = layout?.StyleId;
        if (string.IsNullOrWhiteSpace(style) || !style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
            return;

        text.FontWeight = FontWeight.Bold;
        text.FontSize = style switch
        {
            "Heading1" or "Heading 1" => 20 * _documentZoom,
            "Heading2" or "Heading 2" => 16 * _documentZoom,
            "Heading3" or "Heading 3" => 13 * _documentZoom,
            _ => 11 * _documentZoom
        };
        text.Foreground ??= new SolidColorBrush(Color.Parse("#1E3A5F"));
    }

    private void ApplyFormatting(TextBlock text, RunFormatting formatting)
    {
        text.FontSize = 14 * _documentZoom;
        if (formatting.Bold == true)
            text.FontWeight = FontWeight.Bold;
        if (formatting.Italic == true)
            text.FontStyle = FontStyle.Italic;
        if (double.TryParse(formatting.FontSizeHalfPoints, out var halfPoints))
            text.FontSize = halfPoints / 2 * _documentZoom;
        if (!string.IsNullOrWhiteSpace(formatting.FontColor) && formatting.FontColor.Length == 6)
            text.Foreground = new SolidColorBrush(Color.Parse($"#{formatting.FontColor}"));
        if (formatting.Underline is not null && formatting.Underline != "none")
            text.TextDecorations = TextDecorations.Underline;
        if (formatting.Strikethrough == true)
            text.TextDecorations = new TextDecorationCollection((text.TextDecorations ?? []).Concat(TextDecorations.Strikethrough));
        // Hidden text does not print; it is shown faded so the reviewer can still see and select it.
        if (formatting.Hidden == true)
            text.Opacity = 0.45;
        if (formatting.Subscript == true || formatting.Superscript == true)
        {
            var normalSize = text.FontSize;
            text.FontSize = Math.Max(6, text.FontSize * 0.75);
            text.RenderTransform = new TranslateTransform(0, normalSize * (formatting.Subscript == true ? .25 : -.3));
        }
    }

    private IEnumerable<DisplayFragment> SplitRun(
        TextRun run,
        int paragraphIndex,
        int runStart,
        IReadOnlyList<DifferenceRange> ranges)
    {
        var relevant = ranges.Where(item => item.Range!.ParagraphIndex == paragraphIndex &&
            item.Range.Start < runStart + run.Text.Length &&
            item.Range.Start + item.Range.Length > runStart).ToList();
        // Removed-text carets split the run so they can sit between the right characters.
        var caretBoundaries = ranges
            .Where(item => item.Range!.ParagraphIndex == paragraphIndex && item.Range.Length == 0 &&
                item.Range.Start > runStart && item.Range.Start < runStart + run.Text.Length)
            .Select(item => item.Range!.Start - runStart);
        if (relevant.Count == 0 && !caretBoundaries.Any())
            return [new DisplayFragment(run.Text, null, paragraphIndex, runStart)];

        var boundaries = relevant.SelectMany(item => new[]
        {
            Math.Clamp(item.Range!.Start - runStart, 0, run.Text.Length),
            Math.Clamp(item.Range!.Start + item.Range.Length - runStart, 0, run.Text.Length)
        }).Concat(caretBoundaries).Append(0).Append(run.Text.Length).Distinct().Order().ToList();
        var fragments = new List<DisplayFragment>();
        for (var index = 0; index < boundaries.Count - 1; index++)
        {
            var start = boundaries[index];
            var length = boundaries[index + 1] - start;
            if (length == 0)
                continue;
            var difference = relevant
                .Where(item => item.Range!.Start <= runStart + start && item.Range.Start + item.Range.Length >= runStart + start + length)
                .OrderBy(item => item.DifferenceId == _selectedDifferenceId ? 0 : 1)
                .ThenBy(item => item.Range!.Length)
                .ThenBy(item => item.Category == DifferenceCategory.Table ? 1 : 0)
                .FirstOrDefault();
            fragments.Add(new DisplayFragment(run.Text.Substring(start, length), difference?.DifferenceId, paragraphIndex, runStart + start));
        }

        return fragments;
    }

    private void UpdateButtons()
    {
        CompareButton.IsEnabled = !_comparing && !string.IsNullOrWhiteSpace(_referencePath) && !string.IsNullOrWhiteSpace(_currentPath);
        GeneratePdfButton.IsEnabled = !_comparing && _comparisonResult?.IsSuccess == true;
    }

    private void ShowReadFailure()
    {
        ClearViewer(keepSelectedFileNames: true);

        if (_comparisonResult?.ReferenceDocument is { } reference)
            RenderDocument(ReferenceDocumentPanel, reference, isReference: true, _referenceAnchors);
        else
            RenderUnavailableDocument(ReferenceDocumentPanel, "Reference document is unavailable", "Close the program using the Reference DOCX, then click Compare again.");

        if (_comparisonResult?.CurrentDocument is { } current)
            RenderDocument(CurrentDocumentPanel, current, isReference: false, _currentAnchors);
        else
            RenderUnavailableDocument(CurrentDocumentPanel, "Current document is unavailable", "Close the program using the Current DOCX, then click Compare again.");
    }

    private static void RenderUnavailableDocument(StackPanel target, string title, string instruction)
    {
        target.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFF7ED")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FDBA74")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#9A3412")) },
                    new TextBlock { Text = instruction, TextWrapping = TextWrapping.Wrap }
                }
            }
        });
    }

    private void ClearViewer(bool keepSelectedFileNames = false)
    {
        ReferenceDocumentPanel.Children.Clear();
        CurrentDocumentPanel.Children.Clear();
        _referenceAnchors.Clear();
        _currentAnchors.Clear();
        foreach (var bitmap in _documentBitmaps)
            bitmap.Dispose();
        _documentBitmaps.Clear();
        _navigatorItems = [];
        ShowNavigatorItems(_navigatorItems, null);
        ReferenceOverview.Children.Clear();
        CurrentOverview.Children.Clear();
        _referenceViewport = _currentViewport = null;
        _selectedMatch = null;
        NextReviewButton.IsVisible = false;
        LegendPanel.IsVisible = false;
        ReferenceDocumentName.Text = keepSelectedFileNames ? Path.GetFileName(_referencePath) : "No document selected";
        CurrentDocumentName.Text = keepSelectedFileNames ? Path.GetFileName(_currentPath) : "No document selected";
        ClearDetails();
    }

    /// <summary>Shows the explanation panel instead of its "select a difference" hint.</summary>
    private void ShowDetails()
    {
        DetailEmptyText.IsVisible = false;
        DetailScrollViewer.IsVisible = true;
    }

    private void ClearDetails()
    {
        DetailEmptyText.IsVisible = true;
        DetailScrollViewer.IsVisible = false;
        PositionText.Text = string.Empty;
        DetailOperationText.Text = string.Empty;
        DetailCategoryText.Text = "Select a highlighted difference to see details.";
        DetailConfidenceText.Text = string.Empty;
        DetailSourceTruthText.Text = string.Empty;
        DetailActionText.Text = string.Empty;
        DetailContentKindGrid.IsVisible = false;
        DetailRepresentationText.IsVisible = false;
        DetailLocationText.Text = string.Empty;
        DetailPageNoteText.IsVisible = false;
        DetailReferenceText.Text = string.Empty;
        DetailCurrentText.Text = string.Empty;
        DetailContextText.Text = string.Empty;
        DetailReviewText.IsVisible = false;
        ClearImageDetails();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        ClearImageDetails();
        foreach (var bitmap in _documentBitmaps)
            bitmap.Dispose();
        _autoRefreshCancellation?.Cancel();
        _autoRefreshCancellation?.Dispose();
        _referenceWatcher?.Dispose();
        _currentWatcher?.Dispose();
        base.OnClosed(e);
    }

    private sealed record DifferenceRange(string DifferenceId, DifferenceCategory Category, DocumentRange? Range);
    private sealed record DisplayFragment(string Text, string? DifferenceId, int ParagraphIndex = -1, int Start = 0);
    private sealed record NavigatorItem(
        DocumentDifference Difference,
        string Operation,
        string LineDescription,
        int LineChangeCount,
        string TransitionAndCount,
        string Summary,
        string Before,
        string After)
    {
        /// <summary>Only a change of representation (Image → Table) is worth a line of its own in the list, not "Missing → Text".</summary>
        public bool HasTransition => TransitionAndCount.Contains('→') && !TransitionAndCount.Contains("Missing");

        /// <summary>The stripe colour, matching the highlight in the documents: amber to check, pink missing/formatting, blue otherwise.</summary>
        public IBrush Accent => RequiresReview(Difference) ? AccentReview
            : Difference.Category == DifferenceCategory.Formatting || IsMissingFromCurrent(Difference) ||
              GetDisplayContentKind(Difference, isReference: true) == DifferenceContentKind.Missing ? AccentMissing
            : AccentChange;
    }

    private static readonly IBrush AccentReview = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush AccentMissing = new SolidColorBrush(Color.Parse("#F472B6"));
    private static readonly IBrush AccentChange = new SolidColorBrush(Color.Parse("#60A5FA"));
}
