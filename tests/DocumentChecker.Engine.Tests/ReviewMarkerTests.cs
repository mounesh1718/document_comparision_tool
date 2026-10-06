using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DocumentChecker.Engine.Comparison;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;
using Border = Avalonia.Controls.Border;
using Color = Avalonia.Media.Color;
using Control = Avalonia.Controls.Control;
using InlineRun = Avalonia.Controls.Documents.Run;

namespace DocumentChecker.Engine.Tests;

/// <summary>Every finding from a real comparison must be visible in the viewer, including review items.</summary>
public sealed class ReviewMarkerTests
{
    [Fact]
    public void Edited_paragraphs_pair_by_similarity_and_review_items_have_locations()
    {
        var result = CompareSampleDocuments();

        Assert.DoesNotContain(result.Differences, d => d.ReferenceValue == "Delete" && d.CurrentValue == "A");
        var added = Assert.Single(result.Differences, d => d.Category == DifferenceCategory.ParagraphAdded);
        Assert.Equal("A brand new paragraph.", added.CurrentValue);
        Assert.NotNull(added.CounterpartParagraphIndex);
        var removedWord = Assert.Single(result.Differences, d => d.ReferenceValue == "extra ");
        Assert.Equal("(no corresponding content)", removedWord.CurrentValue);
        var removed = result.Differences.Where(d => d.Category == DifferenceCategory.ParagraphRemoved && d.Property == "Paragraph").ToList();
        Assert.Equal(2, removed.Count);
        Assert.All(removed, d => Assert.NotNull(d.CounterpartParagraphIndex));

        var reviews = result.Differences.Where(d => d.Category == DifferenceCategory.Review).ToList();
        Assert.Contains(reviews, d => d.ReferenceTableIndex == 0 && d.ReferenceLocation?.TableCell is not null);
        Assert.Contains(reviews, d => d.CurrentTableIndex == 0 && d.CurrentLocation?.TableCell is not null);
        Assert.All(reviews, d => Assert.Equal(d.ReferenceContentKind, d.CurrentContentKind));
        // Headers are compared with headers; the page-number footer is identical as a field.
        var header = Assert.Single(result.Differences, d => d.ReferenceValue == "Confidential");
        Assert.Equal("Internal", header.CurrentValue);
        Assert.DoesNotContain(result.Differences, d => d.ReferenceValue.Contains("Page") || d.CurrentValue.Contains("Page"));
    }

    [Fact]
    public void Every_compared_paragraph_knows_its_counterpart_even_when_nothing_changed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Pairs.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            void Body(Body body)
            {
                body.Append(Text("Contract"));
                body.Append(new Table(Row("Item", "Qty"), Row("Sensor", "10")));
                body.Append(Text("Signatures"));
            }
            var reference = Create(Path.Combine(directory, "reference.docx"), Body, headerText: "ACME");
            var current = Create(Path.Combine(directory, "current.docx"), body => { body.Append(Text("Inserted first.")); Body(body); }, headerText: "ACME");

            var result = new DocumentComparisonService().Compare(reference, current);

            int IndexOf(Reading.DocumentModel document, string text) => document.Paragraphs.First(p => p.Text == text).Index;
            foreach (var text in new[] { "Contract", "Sensor", "10", "Signatures", "ACME" })
            {
                var referenceIndex = IndexOf(result.ReferenceDocument!, text);
                var currentIndex = IndexOf(result.CurrentDocument!, text);
                Assert.Equal(currentIndex, result.CounterpartOf(referenceIndex, isReference: true));
                Assert.Equal(referenceIndex, result.CounterpartOf(currentIndex, isReference: false));
            }
            // The inserted paragraph has no counterpart.
            Assert.Null(result.CounterpartOf(IndexOf(result.CurrentDocument!, "Inserted first."), isReference: false));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Paragraphs_rewritten_in_place_still_show_their_changed_words()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Rewrite.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var reference = Create(Path.Combine(directory, "reference.docx"), body =>
            {
                body.Append(Text("Start"));
                body.Append(Text("Prices are fixed for one year."));
                body.Append(Text("Warranty: 12 months."));
                body.Append(Text("Draft"));
                body.Append(Text("End"));
            }, headerText: "Same");
            var current = Create(Path.Combine(directory, "current.docx"), body =>
            {
                body.Append(Text("Start"));
                body.Append(Text("Prices may change quarterly."));
                body.Append(Text("Guarantee covers 24 months."));
                body.Append(Text("Approved"));
                body.Append(Text("End"));
            }, headerText: "Same");

            var result = new DocumentComparisonService().Compare(reference, current);

            // Little shared wording, but the same position: compared word by word, never "deleted + added".
            Assert.DoesNotContain(result.Differences, d => d.Category is DifferenceCategory.ParagraphAdded or DifferenceCategory.ParagraphRemoved);
            Assert.Contains(result.Differences, d => d.ReferenceValue == "12" && d.CurrentValue == "24");
            Assert.Contains(result.Differences, d => d.ReferenceValue == "Draft" && d.CurrentValue == "Approved");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Runs inside the shared UI test window; Avalonia can only be set up once per process.</summary>
    internal static void VerifyInWindow(MainWindow window, Action<MainWindow> layout)
    {

        var result = CompareSampleDocuments();
        Set(window, "_comparisonResult", result);
        Set(window, "_selectedDifferenceId", null);
        var items = Invoke(window, "BuildNavigatorItems", result.Differences);
        Set(window, "_navigatorItems", items);
        window.FindControl<ListBox>("DifferenceList")!.ItemsSource = (System.Collections.IEnumerable)items;
        Invoke(window, "RenderDocuments");
        Invoke(window, "UpdateReviewButton");
        layout(window);

        AssertPanesAligned(window);

        // The merged "Total" cell covers both table columns, as in Word.
        var totalCell = window.FindControl<StackPanel>("ReferenceDocumentPanel")!.GetVisualDescendants().OfType<Border>()
            .First(border => border.Parent is Grid && border.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Inlines?.OfType<InlineRun>().Any(run => run.Text == "Total") == true));
        Assert.Equal(2, Grid.GetColumnSpan(totalCell));

        var left = (Dictionary<string, Control>)Get(window, "_referenceAnchors");
        var right = (Dictionary<string, Control>)Get(window, "_currentAnchors");
        // Every finding can be found in at least one document.
        Assert.All(result.Differences, d => Assert.True(left.ContainsKey(d.Id) || right.ContainsKey(d.Id), $"{d.Id} {d.Property} is not shown"));
        // Content removed or added on one side is also marked where it belongs on the other side.
        Assert.All(result.Differences.Where(d => d.Category == DifferenceCategory.ParagraphRemoved && d.Property == "Paragraph"),
            d => Assert.True(left.ContainsKey(d.Id) && right.ContainsKey(d.Id), $"{d.Id} lacks a paired marker"));
        Assert.All(result.Differences.Where(d => d.Category == DifferenceCategory.ParagraphAdded),
            d => Assert.True(left.ContainsKey(d.Id) && right.ContainsKey(d.Id), $"{d.Id} lacks a paired marker"));
        Assert.True(right.ContainsKey(result.Differences.Single(d => d.ReferenceValue == "extra ").Id));

        // Review items are amber in the documents and in the strips beside the scroll bars.
        var amber = Color.Parse("#F59E0B");
        var reviewCount = result.Differences.Count(d => d.ReviewNote is not null);
        Assert.Contains(window.FindControl<StackPanel>("ReferenceDocumentPanel")!.Children.OfType<Border>(),
            border => border.BorderBrush is ISolidColorBrush brush && brush.Color == amber);
        var marks = window.FindControl<Canvas>("ReferenceOverview")!.Children.Concat(window.FindControl<Canvas>("CurrentOverview")!.Children)
            .OfType<Border>().Where(border => border.Background is ISolidColorBrush brush && brush.Color == amber).ToList();
        Assert.Equal(reviewCount, marks.Count);

        var button = window.FindControl<Button>("NextReviewButton")!;
        Assert.True(button.IsVisible);
        Assert.Equal($"Next to review ({reviewCount})", button.Content);
        var visited = new HashSet<string>();
        for (var index = 0; index < reviewCount; index++)
        {
            Invoke(window, "NextReview_Click", null, new Avalonia.Interactivity.RoutedEventArgs());
            visited.Add((string)Get(window, "_selectedDifferenceId"));
        }
        Assert.Equal(reviewCount, visited.Count);
        Assert.Equal($"Next to review ({reviewCount} of {reviewCount})", button.Content);
        // Header and footer areas are drawn in both panes, and a header change is highlighted and explained.
        foreach (var panelName in new[] { "ReferenceDocumentPanel", "CurrentDocumentPanel" })
        {
            // Document text is drawn as inline runs; part labels use TextBlock.Text.
            var labels = window.FindControl<StackPanel>(panelName)!.GetVisualDescendants().OfType<TextBlock>()
                .SelectMany(t => t.Inlines is { Count: > 0 } inlines ? inlines.OfType<InlineRun>().Select(run => run.Text) : [t.Text]).ToList();
            Assert.Contains("HEADER", labels);
            Assert.Contains("FOOTER", labels);
            Assert.Contains("[Page number]", labels);
            Assert.Contains("[Page count]", labels);
        }
        var header = result.Differences.Single(d => d.ReferenceValue == "Confidential");
        Assert.True(left.ContainsKey(header.Id) && right.ContainsKey(header.Id));
        Invoke(window, "SelectNavigatorDifference", header);
        Assert.Equal("Confidential", window.FindControl<SelectableTextBlock>("DetailReferenceText")!.Text);
        Assert.Equal("Internal", window.FindControl<SelectableTextBlock>("DetailCurrentText")!.Text);
        Assert.Equal("Both documents: Header, Paragraph 1", window.FindControl<TextBlock>("DetailLocationText")!.Text);
        Assert.Contains("repeats on every page", window.FindControl<TextBlock>("DetailPageNoteText")!.Text);

        // Clicking unchanged content confirms the match and outlines the same content in both documents.
        ClickText(window, "ReferenceDocumentPanel", "Contract");
        Assert.Equal("No difference", window.FindControl<TextBlock>("DetailOperationText")!.Text);
        Assert.StartsWith("MATCH", window.FindControl<TextBlock>("DetailConfidenceText")!.Text);
        Assert.Equal("Contract", window.FindControl<SelectableTextBlock>("DetailReferenceText")!.Text);
        Assert.Equal("Contract", window.FindControl<SelectableTextBlock>("DetailCurrentText")!.Text);
        Assert.StartsWith("Both documents:", window.FindControl<TextBlock>("DetailLocationText")!.Text);
        Assert.Null(Get(window, "_selectedDifferenceId", allowNull: true));
        layout(window);
        Assert.True(((Dictionary<string, Control>)Get(window, "_referenceAnchors")).ContainsKey("MATCH"));
        Assert.True(((Dictionary<string, Control>)Get(window, "_currentAnchors")).ContainsKey("MATCH"));

        // An unchanged cell is paired with its cell in the other document, which sits in a different paragraph slot.
        ClickText(window, "CurrentDocumentPanel", "Sensor");
        Assert.Equal("Sensor", window.FindControl<SelectableTextBlock>("DetailReferenceText")!.Text);
        Assert.Equal("Sensor", window.FindControl<SelectableTextBlock>("DetailCurrentText")!.Text);
        Assert.Contains("This table has", window.FindControl<TextBlock>("DetailSourceTruthText")!.Text);
        Assert.Equal(result.Differences.Count, window.FindControl<ListBox>("DifferenceList")!.ItemCount);
        Assert.Null(window.FindControl<ListBox>("DifferenceList")!.SelectedItem);
        if (Environment.GetEnvironmentVariable("DOCHECK_MATCH_CAPTURE") is { Length: > 0 } matchCapture)
        {
            layout(window);
            using var bitmap = new RenderTargetBitmap(new PixelSize(1464, 840), new Vector(96, 96));
            bitmap.Render((Control)window.Content!);
            bitmap.Save(matchCapture, new PngBitmapEncoderOptions());
        }

        // Selecting a real difference afterwards removes the match outline.
        Invoke(window, "SelectNavigatorDifference", header);
        layout(window);
        Assert.False(((Dictionary<string, Control>)Get(window, "_referenceAnchors")).ContainsKey("MATCH"));
        Assert.Equal("Confidential", window.FindControl<SelectableTextBlock>("DetailReferenceText")!.Text);
        Assert.Equal("All changes · click a highlighted value to focus", window.FindControl<TextBlock>("SelectionScopeText")!.Text);
        // Re-drawing for a selection keeps the panes aligned.
        AssertPanesAligned(window);

        layout(window);
        var capture = Environment.GetEnvironmentVariable("DOCHECK_MARKERS_CAPTURE");
        if (!string.IsNullOrEmpty(capture))
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(1464, 840), new Vector(96, 96));
            bitmap.Render((Control)window.Content!);
            bitmap.Save(capture, new PngBitmapEncoderOptions());
        }
    }

    private static ComparisonResult CompareSampleDocuments()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Markers.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var reference = Create(Path.Combine(directory, "reference.docx"), body =>
            {
                body.Append(Heading("Contract"));
                body.Append(Text("Payment within 30 days of invoice."));
                body.Append(Text("This paragraph is removed later."));
                body.Append(new Paragraph());
                body.Append(new Paragraph(new Run(new RunProperties(new Bold()), new Text("Important notice"))));
                body.Append(new Table(Row("Item", "Qty"), Row("Sensor", "10"), Row("Cable", "4"), MergedRow("Total"), ControlledRow("Signed")));
                body.Append(Text("Delete the word extra here."));
            }, headerText: "ACME — Confidential");
            var current = Create(Path.Combine(directory, "current.docx"), body =>
            {
                body.Append(Heading("Contract"));
                body.Append(Text("Payment within 45 days of invoice."));
                body.Append(Text("Important notice"));
                body.Append(Text("A brand new paragraph."));
                body.Append(new Table(Row("Item", "Qty"), Row("Sensor", "15"), MergedRow("Total"), ControlledRow("Signed")));
                body.Append(Text("Delete the word here."));
            }, headerText: "ACME — Internal");
            return new DocumentComparisonService().Compare(reference, current);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Paragraph Text(string text) => new(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text(text)));
    private static Paragraph Heading(string text) => new(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new DocumentFormat.OpenXml.Wordprocessing.Text(text)));
    private static TableRow Row(params string[] cells) => new(cells.Select(cell => new TableCell(Text(cell))));
    private static TableRow MergedRow(string text) => new(new TableCell(new TableCellProperties(new GridSpan { Val = 2 }), Text(text)));
    // A row wrapped in a content control is not read, so its table still needs review.
    private static SdtRow ControlledRow(string text) => new(new SdtContentRow(Row(text, "-")));

    private static string Create(string path, Action<Body> add, string headerText)
    {
        using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        add(body);
        var header = main.AddNewPart<HeaderPart>();
        header.Header = new Header(Text(headerText));
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new Footer(new Paragraph(
            new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Page ") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
            new SimpleField(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("3"))) { Instruction = " PAGE " },
            new Run(new DocumentFormat.OpenXml.Wordprocessing.Text(" of ") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
            new SimpleField(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("12"))) { Instruction = " NUMPAGES " }));
        body.Append(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) }));
        main.Document.Save();
        return path;
    }

    /// <summary>Matching content starts at the same height in both panes, and both panes scroll together.</summary>
    private static void AssertPanesAligned(MainWindow window)
    {
        Assert.True((bool)Get(window, "_panesAligned"));
        var referencePanel = window.FindControl<StackPanel>("ReferenceDocumentPanel")!;
        var currentPanel = window.FindControl<StackPanel>("CurrentDocumentPanel")!;
        var reference = (List<(string Key, Control Control)>)Get(window, "_referenceSync");
        var current = ((List<(string Key, Control Control)>)Get(window, "_currentSync")).ToDictionary(item => item.Key, item => item.Control);
        var shared = reference.Where(item => current.ContainsKey(item.Key)).ToList();
        // Paragraphs, the removed paragraph and its "missing here" gap, the table and the header all line up.
        Assert.True(shared.Count >= 10, $"only {shared.Count} shared blocks");
        foreach (var (key, control) in shared)
        {
            var referenceTop = control.TranslatePoint(default, referencePanel)!.Value.Y;
            var currentTop = current[key].TranslatePoint(default, currentPanel)!.Value.Y;
            Assert.True(Math.Abs(referenceTop - currentTop) < 1, $"{key}: Reference at {referenceTop:0}, Current at {currentTop:0}");
        }
        Assert.Equal(referencePanel.Bounds.Height, currentPanel.Bounds.Height, 0.5);

        // The table row missing from Current keeps its place, so the rows below it stay level.
        var currentTexts = currentPanel.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("Row missing here: Cable · 4", currentTexts);
        var grids = ((System.Collections.IDictionary)Get(window, "_referenceTableGrids")).Values.Cast<Grid>().Single();
        var currentGrid = ((System.Collections.IDictionary)Get(window, "_currentTableGrids")).Values.Cast<Grid>().Single();
        Assert.Equal(grids.RowDefinitions.Count, currentGrid.RowDefinitions.Count);
        for (var row = 0; row < grids.RowDefinitions.Count; row++)
            Assert.Equal(grids.RowDefinitions[row].ActualHeight, currentGrid.RowDefinitions[row].ActualHeight, 0.5);

        // Scrolling one pane moves the other by the same amount.
        var referenceViewer = window.FindControl<ScrollViewer>("ReferenceScrollViewer")!;
        var currentViewer = window.FindControl<ScrollViewer>("CurrentScrollViewer")!;
        referenceViewer.Offset = new Vector(referenceViewer.Offset.X, 120);
        Invoke(window, "SynchronizeScroll", referenceViewer, currentViewer);
        Assert.Equal(referenceViewer.Offset.Y, currentViewer.Offset.Y, 0.5);
        Assert.True(currentViewer.Offset.Y > 0);
        referenceViewer.Offset = default;
        currentViewer.Offset = default;
    }

    /// <summary>Left-clicks the text control showing exactly this text in a document pane.</summary>
    private static void ClickText(MainWindow window, string panelName, string text)
    {
        var target = window.FindControl<StackPanel>(panelName)!.GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.Inlines is { Count: > 0 } inlines && string.Concat(inlines.OfType<InlineRun>().Select(run => run.Text)) == text);
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
        target.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(target, pointer, window, new Point(), 0,
            new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
            Avalonia.Input.KeyModifiers.None, 1));
    }

    private static object? Get(object target, string name, bool allowNull) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object Invoke(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!;
}
