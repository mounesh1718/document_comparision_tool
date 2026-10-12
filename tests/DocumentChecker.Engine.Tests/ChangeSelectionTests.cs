using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using Xunit;

namespace DocumentChecker.Engine.Tests;

public sealed class ChangeSelectionTests
{
    [Fact]
    [Trait("TC", "TC-UI-01")]
    public void Clicking_a_table_value_or_image_region_focuses_its_exact_change_and_supports_reselection()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifySelection(); }
            catch (Exception exception) { failure = exception; }
        });
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "UI selection test timed out.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifySelection()
    {
        // Windows uses the real desktop platform. Elsewhere the window is drawn off screen by the headless platform
        // with the same Skia renderer, because macOS only allows windows on the process's main thread.
        var builder = AppBuilder.Configure<App>();
        (OperatingSystem.IsWindows()
            ? builder.UsePlatformDetect()
            : builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }))
            .SetupWithoutStarting();
        var window = new MainWindow();
        window.Position = new PixelPoint(-30000, -30000);
        window.ShowInTaskbar = false;
        window.Show();
        try
        {
            var left = CreateDocument("Reference.docx", "10", "30");
            var right = CreateDocument("Current.docx", "15", "45");
            var text = new DocumentDifference("TXT-001", DifferenceCategory.Text, "Text", "10", "15",
                new(3, 0, new(0, 1, 1)), new(3, 0, new(0, 1, 1)), new(3, 0, 2), new(3, 0, 2), "Sensor quantity");
            var image = new DocumentDifference("IMG-002", DifferenceCategory.Image, "Text in image", "30", "45",
                new(4, 0, Page: new(8, PageNumberSource.SavedLayout)), new(4, 0, Page: new(9, PageNumberSource.SavedLayout)), null, null, "Image row: Payment terms: 30 days\nCurrent row: Payment terms: 45 days",
                "IMAGE-1", "IMAGE-1", ImageRegion: new(230, 30, 40, 30), CurrentImageRegion: new(230, 30, 40, 30),
                IsRecognizedText: true, ReviewNote: "Text recognized from an image. Verify the enlarged crops.");
            var structure = new DocumentDifference("TBL-003", DifferenceCategory.Table, "Rows", "2", "3",
                new(0, 0), new(0, 0), null, null, "Table structure", ReferenceTableIndex: 0, CurrentTableIndex: 0);
            var result = new ComparisonResult(ComparisonStatus.Different, left, right, [text, image, structure], [], null, null);
            Set(window, "_comparisonResult", result);
            window.FindControl<TextBlock>("ReferenceDocumentName")!.Text = left.FileName;
            window.FindControl<TextBlock>("CurrentDocumentName")!.Text = right.FileName;
            window.FindControl<TextBlock>("StatusText")!.Text = "Select a highlighted value to review its exact change.";
            var items = Invoke(window, "BuildNavigatorItems", result.Differences);
            Set(window, "_navigatorItems", items);
            window.FindControl<ListBox>("DifferenceList")!.ItemsSource = (System.Collections.IEnumerable)items;
            Invoke(window, "RenderDocuments");
            Layout(window);

            ClickAnchor(window, text.Id, true);
            AssertDetails(window, "10", "15");
            Assert.True(window.FindControl<Grid>("DetailContentKindGrid")!.IsVisible);
            Assert.Equal("Reference structure: Table", window.FindControl<TextBlock>("DetailReferenceContentKindText")!.Text);
            Assert.Equal("Current structure: Table", window.FindControl<TextBlock>("DetailCurrentContentKindText")!.Text);
            Assert.StartsWith("READ FROM DOCX", window.FindControl<TextBlock>("DetailConfidenceText")!.Text);
            Assert.Contains("update Current", window.FindControl<TextBlock>("DetailActionText")!.Text);
            Assert.Contains("Reference requires “10”; Current contains “15”", window.FindControl<TextBlock>("DetailSourceTruthText")!.Text);
            Assert.Contains("Sensor", window.FindControl<TextBlock>("DetailLocationText")!.Text);
            Assert.Contains("Qty", window.FindControl<TextBlock>("DetailLocationText")!.Text);
            Assert.False(window.FindControl<Image>("DetailReferenceImage")!.IsVisible);
            var textAnchor = (Border)((Dictionary<string, Control>)Get(window, "_referenceAnchors"))[text.Id];
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)textAnchor.Background!).Color);
            Assert.Equal(0, textAnchor.BorderThickness.Top);
            Assert.Equal(2, textAnchor.BorderThickness.Bottom);
            // A table-level handler must not replace the clicked cell's selection.
            Assert.Equal(text.Id, Get(window, "_selectedDifferenceId"));

            ClickAnchor(window, image.Id, false);
            AssertDetails(window, "30", "45");
            Assert.True(window.FindControl<Image>("DetailReferenceImage")!.IsVisible);
            Assert.NotNull(window.FindControl<Image>("DetailCurrentImage")!.Source);
            Assert.StartsWith("REVIEW REQUIRED", window.FindControl<TextBlock>("DetailConfidenceText")!.Text);
            Assert.Contains("inspect the original evidence", window.FindControl<TextBlock>("DetailActionText")!.Text);
            Assert.Contains("Reference: Page 8", window.FindControl<TextBlock>("DetailLocationText")!.Text);
            Assert.Contains("Current: Page 9", window.FindControl<TextBlock>("DetailLocationText")!.Text);
            var imageAnchor = (Border)((Dictionary<string, Control>)Get(window, "_currentAnchors"))[image.Id];
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)imageAnchor.Background!).Color);
            Assert.Equal(new Thickness(0), imageAnchor.BorderThickness);
            ClickAnchor(window, image.Id, true);
            AssertDetails(window, "30", "45");
            Layout(window);
            var capture = Environment.GetEnvironmentVariable("DOCHECK_UI_CAPTURE");
            if (!string.IsNullOrEmpty(capture))
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize(1464, 840), new Vector(96, 96));
                bitmap.Render((Control)window.Content!);
                bitmap.Save(capture, new PngBitmapEncoderOptions());
            }

            Invoke(window, "HighlightToggle_Click", null, new Avalonia.Interactivity.RoutedEventArgs());
            Assert.Equal("Show highlights", window.FindControl<Button>("HighlightToggleButton")!.Content);
            var hiddenMarkers = window.FindControl<StackPanel>("CurrentDocumentPanel")!.GetVisualDescendants()
                .OfType<Border>().Where(border => border.Parent is Canvas && !border.IsHitTestVisible).ToList();
            Assert.NotEmpty(hiddenMarkers);
            Assert.All(hiddenMarkers, marker => Assert.False(marker.IsVisible));
            ClickAnchor(window, image.Id, false);
            AssertDetails(window, "30", "45");
            Assert.Equal("Show highlights", window.FindControl<Button>("HighlightToggleButton")!.Content);

            Invoke(window, "ShowAllChanges_Click", null, new Avalonia.Interactivity.RoutedEventArgs());
            Assert.Equal(3, window.FindControl<ListBox>("DifferenceList")!.ItemCount);
            Invoke(window, "MoveSelection", 1);
            Assert.Equal(structure.Id, Get(window, "_selectedDifferenceId"));

            // A mixed-format finding must be selectable from either the image crop or the editable table value.
            var mixed = image with { Id = "MIX-001", Property = "Image / table value (OCR)", CurrentImageId = null,
                CurrentImageRegion = null, CurrentRange = new(3, 0, 2), CurrentLocation = new(3, 0, new(0, 1, 1)), CurrentValue = "15",
                CurrentTableIndex = 0, ReferenceContentKind = DifferenceContentKind.Image, CurrentContentKind = DifferenceContentKind.Table };
            Invoke(window, "HighlightToggle_Click", null, new Avalonia.Interactivity.RoutedEventArgs());
            Assert.Equal("Hide highlights", window.FindControl<Button>("HighlightToggleButton")!.Content);
            Set(window, "_comparisonResult", result with { Differences = [mixed] });
            Set(window, "_navigatorItems", Invoke(window, "BuildNavigatorItems", new List<DocumentDifference> { mixed }));
            Invoke(window, "RenderDocuments");
            Layout(window);
            var reviewGrey = Color.Parse("#64748B");
            Assert.Contains(window.FindControl<StackPanel>("ReferenceDocumentPanel")!.GetVisualDescendants().OfType<Border>(),
                border => border.BorderBrush is ISolidColorBrush brush && brush.Color == reviewGrey);
            Assert.Contains(window.FindControl<StackPanel>("CurrentDocumentPanel")!.GetVisualDescendants().OfType<Border>(),
                border => border.BorderBrush is ISolidColorBrush brush && brush.Color == reviewGrey);
            ClickAnchor(window, mixed.Id, true);
            AssertDetails(window, "30", "15");
            Assert.True(window.FindControl<Image>("DetailReferenceImage")!.IsVisible);
            Assert.False(window.FindControl<Image>("DetailCurrentImage")!.IsVisible);
            Assert.True(window.FindControl<Grid>("DetailContentKindGrid")!.IsVisible);
            Assert.Equal("Reference structure: Image", window.FindControl<TextBlock>("DetailReferenceContentKindText")!.Text);
            Assert.Equal("Current structure: Table", window.FindControl<TextBlock>("DetailCurrentContentKindText")!.Text);
            Assert.Contains("Image recognition read '30'", window.FindControl<TextBlock>("DetailSourceTruthText")!.Text);
            Assert.Contains("Reference value was found in an image", window.FindControl<TextBlock>("DetailRepresentationText")!.Text);
            Assert.Contains("Table 1, Row 2, Column 2", window.FindControl<TextBlock>("DetailRepresentationText")!.Text);
            var mixedNavigator = (System.Collections.IEnumerable)window.FindControl<ListBox>("DifferenceList")!.ItemsSource!;
            Assert.Contains("Image → Table", mixedNavigator.Cast<object>().Single()!.GetType().GetProperty("TransitionAndCount")!.GetValue(mixedNavigator.Cast<object>().Single())!.ToString());
            Assert.Contains("Column 2", window.FindControl<TextBlock>("DetailLocationText")!.Text);
            ClickAnchor(window, mixed.Id, false);
            AssertDetails(window, "30", "15");
            Assert.Contains("Current value is in Table 1, Row 2, Column 2", window.FindControl<TextBlock>("DetailRepresentationText")!.Text);

            // A row absent from Current receives a pink Reference square and a pink, clickable Current-table outline.
            var missing = new DocumentDifference("TBL-MISS-001", DifferenceCategory.Table, "Missing row value", "Sensor", "(no corresponding content)",
                new(2, 0, new(0, 1, 0)), null, new(2, 0, 6), null, "Approved table row is absent from Current.",
                ReferenceTableIndex: 0, CurrentTableIndex: 0,
                ReferenceContentKind: DifferenceContentKind.Table, CurrentContentKind: DifferenceContentKind.Missing);
            Set(window, "_comparisonResult", result with { Differences = [missing] });
            Set(window, "_navigatorItems", Invoke(window, "BuildNavigatorItems", new List<DocumentDifference> { missing }));
            Invoke(window, "RenderDocuments");
            Layout(window);
            var missingPink = Color.Parse("#F472B6");
            var calloutPink = Color.Parse("#DB2777");
            Assert.Contains(window.FindControl<StackPanel>("ReferenceDocumentPanel")!.GetVisualDescendants().OfType<Border>(),
                border => border.BorderBrush is ISolidColorBrush brush && brush.Color == missingPink);
            Assert.Contains(window.FindControl<StackPanel>("CurrentDocumentPanel")!.GetVisualDescendants().OfType<Border>(),
                border => border.BorderBrush is ISolidColorBrush brush && brush.Color == calloutPink);
            Assert.DoesNotContain(window.FindControl<StackPanel>("CurrentDocumentPanel")!.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("MISSING FROM CURRENT", StringComparison.Ordinal) == true);
            ClickAnchor(window, missing.Id, false);
            Assert.Equal("Sensor", window.FindControl<SelectableTextBlock>("DetailReferenceText")!.Text);
            Assert.Equal("Not present", window.FindControl<SelectableTextBlock>("DetailCurrentText")!.Text);
            Assert.Contains("Missing from Current", window.FindControl<TextBlock>("DetailSourceTruthText")!.Text);

            // An alignment change is boxed in pink and given a pink underline; the explanation stays in Change Tracker.
            var alignment = new DocumentDifference("PAR-ALIGN-001", DifferenceCategory.Formatting, "Paragraph alignment", "center", "right",
                new(2, 0, new(0, 1, 0)), new(2, 0, new(0, 1, 0)), new(2, 0, 6), new(2, 0, 6), "Alignment of the approved table row differs.");
            Set(window, "_comparisonResult", result with { Differences = [alignment] });
            Set(window, "_navigatorItems", Invoke(window, "BuildNavigatorItems", new List<DocumentDifference> { alignment }));
            Invoke(window, "RenderDocuments");
            Layout(window);
            Assert.Contains(window.FindControl<StackPanel>("ReferenceDocumentPanel")!.GetVisualDescendants().OfType<Border>(),
                border => border.BorderThickness.Bottom == 3 && border.BorderBrush is ISolidColorBrush brush && brush.Color == calloutPink);
            ClickAnchor(window, alignment.Id, true);
            Assert.Contains("Alignment is wrong", window.FindControl<TextBlock>("DetailSourceTruthText")!.Text);

            var normal = new RunFormatting(null, null, null, null, "none", null, null, null, null, null, null);
            var referenceFormula = new ParagraphBlock(0, [new(0, "Water: H", normal), new(1, "2", normal with { Subscript = true }), new(2, "O", normal)]);
            var currentFormula = new ParagraphBlock(0, [new(0, "Water: H2O", normal)]);
            var subscript = new DocumentDifference("FORMULA-SUB", DifferenceCategory.Formatting, "Subscript", "On", "Off",
                new(0, 1), new(0, 0), new(0, 8, 1), new(0, 8, 1), "Water: H2O");
            Set(window, "_comparisonResult", result with { ReferenceDocument = new("Reference.docx", [referenceFormula]), CurrentDocument = new("Current.docx", [currentFormula]), Differences = [subscript] });
            Set(window, "_navigatorItems", Invoke(window, "BuildNavigatorItems", new List<DocumentDifference> { subscript }));
            Invoke(window, "RenderDocuments");
            Layout(window);
            ClickAnchor(window, subscript.Id, false);
            Assert.Contains("'2' should appear below", window.FindControl<TextBlock>("DetailSourceTruthText")!.Text);
            var referenceTexts = window.FindControl<StackPanel>("ReferenceDocumentPanel")!.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(referenceTexts, t => t.RenderTransform is TranslateTransform transform && transform.Y > 0);
            Assert.All(referenceTexts, t => Assert.True(t.TextDecorations is null || t.TextDecorations.Count == 0));
            Layout(window);
            var formulaCapture = Environment.GetEnvironmentVariable("DOCHECK_FORMULA_CAPTURE");
            if (!string.IsNullOrEmpty(formulaCapture))
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize(1464, 840), new Vector(96, 96));
                bitmap.Render((Control)window.Content!);
                bitmap.Save(formulaCapture, new PngBitmapEncoderOptions());
            }
            var space = new DocumentDifference("SPACE", DifferenceCategory.Text, "Text", " ", "(no corresponding content)",
                new(3, 0, new(0, 1, 1)), new(3, 0, new(0, 1, 1)), new(3, 3, 1), new(3, 3, 0), "Medication spacing");
            Set(window, "_comparisonResult", result with { ReferenceDocument = CreateDocument("Reference.docx", "500 mg", "30"), CurrentDocument = CreateDocument("Current.docx", "500mg", "30"), Differences = [space] });
            Set(window, "_navigatorItems", Invoke(window, "BuildNavigatorItems", new List<DocumentDifference> { space }));
            Invoke(window, "RenderDocuments");
            Layout(window);
            window.FindControl<Expander>("DetailMoreExpander")!.IsExpanded = true;
            ClickAnchor(window, space.Id, true);
            Assert.Equal("500 mg", window.FindControl<SelectableTextBlock>("DetailReferenceText")!.Text);
            Assert.Equal("500mg", window.FindControl<SelectableTextBlock>("DetailCurrentText")!.Text);
            Assert.Equal("Current structure: Table", window.FindControl<TextBlock>("DetailCurrentContentKindText")!.Text);
            Assert.Contains("missing a space", window.FindControl<TextBlock>("DetailSourceTruthText")!.Text);
            Assert.StartsWith("Both documents:", window.FindControl<TextBlock>("DetailLocationText")!.Text);
            Assert.False(window.FindControl<Expander>("DetailMoreExpander")!.IsExpanded);

            ReviewMarkerTests.VerifyInWindow(window, Layout);
        }
        finally { window.Close(); }
    }

    private static void AssertDetails(MainWindow window, string before, string after)
    {
        Assert.Equal(before, window.FindControl<SelectableTextBlock>("DetailReferenceText")!.Text);
        Assert.Equal(after, window.FindControl<SelectableTextBlock>("DetailCurrentText")!.Text);
        Assert.Equal(1, window.FindControl<ListBox>("DifferenceList")!.ItemCount);
        Assert.True(window.FindControl<Button>("ShowAllChangesButton")!.IsVisible);
    }

    private static void ClickAnchor(MainWindow window, string id, bool reference)
    {
        var anchors = (Dictionary<string, Control>)Get(window, reference ? "_referenceAnchors" : "_currentAnchors");
        var control = anchors[id];
        var pointer = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);
        var args = new PointerPressedEventArgs(control, pointer, window, new Point(), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1);
        control.RaiseEvent(args);
    }

    private static void Layout(MainWindow window)
    {
        var root = (Control)window.Content!;
        root.Measure(new Size(1464, 840));
        root.Arrange(new Rect(0, 0, 1464, 840));
        root.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
    }

    private static DocumentModel CreateDocument(string name, string quantity, string days)
    {
        var format = new RunFormatting(null, null, null, null, null, null, null, null, null, null, null);
        ParagraphBlock Cell(int index, int row, int column, string value) => new(index, [new(0, value, format)], TableLocation: new(0, row, column));
        var header = new[] { Cell(0, 0, 0, "Item"), Cell(1, 0, 1, "Qty") };
        var values = new[] { Cell(2, 1, 0, "Sensor"), Cell(3, 1, 1, quantity) };
        var imageParagraph = new ParagraphBlock(4, [new(0, "", format, ["IMAGE-1"])]);
        var image = new DocumentImage("IMAGE-1", 4, 0, ContentDifferenceTests.CreateTextImage($"Payment terms: {days} days"), "image/png", 10000, 10000);
        var table = new TableDisplayBlock([
            new(header.Select(paragraph => new DocumentTableCell([paragraph])).ToList()),
            new(values.Select(paragraph => new DocumentTableCell([paragraph])).ToList())]);
        return new(name, header.Concat(values).Append(imageParagraph).ToList(), [image], [table, new ParagraphDisplayBlock(imageParagraph)]);
    }

    private static object Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object Invoke(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!;
}
