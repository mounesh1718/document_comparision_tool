using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.ExactView;
using DocumentChecker.Engine.History;
using DocumentChecker.Engine.Reading;
using DocumentChecker.Engine.Reporting;
using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>Exact pages: reading laid-out pages with PDFium and finding each difference on them.</summary>
public sealed class ExactViewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Exact.{Guid.NewGuid():N}");

    [Fact]
    [Trait("TC", "TC-EXV-01")]
    public void Pdfium_draws_pages_and_reads_their_characters()
    {
        Directory.CreateDirectory(_directory);
        var pdfPath = Path.Combine(_directory, "report.pdf");
        var result = new ComparisonResult(ComparisonStatus.Different, new DocumentModel("Reference.docx", []), new DocumentModel("Current.docx", []),
            [new DocumentDifference("TXT-001", DifferenceCategory.Text, "Text", "30", "45", null, null, null, null, "Payment within 30 days")], [], "a", "b");
        new ComparisonReportGenerator().Generate(result, pdfPath);

        var pages = Pdfium.ReadPages(File.ReadAllBytes(pdfPath));

        var page = Assert.Single(pages);
        Assert.InRange(page.Width, 590, 600);   // A4 is 595 points wide
        Assert.Equal(page.PixelWidth * page.PixelHeight * 4, page.Pixels.Length);
        Assert.Contains(page.Pixels, value => value < 128);   // something dark is drawn on the white page
        var text = string.Concat(page.Characters.Select(character => character.Character));
        Assert.Contains("DOCX Comparison Report", text);
        Assert.All(page.Characters.Where(character => !char.IsWhiteSpace(character.Character)),
            character => Assert.InRange(character.Y, 0, page.Height));
    }

    [Fact]
    [Trait("TC", "TC-EXV-02")]
    public void Differences_are_found_on_the_page_where_they_are_printed()
    {
        // A header, a title, a short value repeated elsewhere, a symbol the PDF draws without text, and hidden text.
        var document = Model(
            ("ACME Ltd — Confidential", true),
            ("Finished Product Specification", false),
            ("Total 3.0", false),
            ("Assay 95.0% of label claim, total 3.0 units", false),
            ("Limit for total impurities: ≤ 1.0%.", false),
            ("Keep out of reach of children.", false));
        var pages = new[]
        {
            Page(("ACME Ltd — Confidential", 30), ("Finished Product Specification", 80), ("Total 3.0", 110),
                ("Assay 95.0% of label claim, total 3.0 units", 140), ("Limit for total impurities: 1.0%.", 170)),
        };
        var locator = new PageLocator(document, pages);
        DocumentDifference Text(int paragraph, int start, int length) =>
            new("TXT", DifferenceCategory.Text, "Text", "a", "b", new(paragraph, 0), new(paragraph, 0), new(paragraph, start, length), new(paragraph, start, length), "");

        // "95.0%" in paragraph 3 (characters 6-10): one box on its line, starting where "95.0%" is printed.
        var value = Assert.Single(locator.Locate(Text(3, 6, 5), isReference: true));
        Assert.Equal(140, value.Y, 0.1);
        Assert.Equal(10 + 5 * CharacterWidth, value.X, 0.1);   // "Assay" = 5 printed characters before it
        Assert.Equal(5 * CharacterWidth, value.Width, 0.1);

        // The short "3.0" of paragraph 2 is found on its own line, not inside the longer paragraph after it.
        Assert.Equal(110, Assert.Single(locator.Locate(Text(2, 6, 3), isReference: true)).Y, 0.1);

        // The header is found on the page; "Confidential" sits after "ACME Ltd —".
        Assert.Equal(30, Assert.Single(locator.Locate(Text(0, 11, 12), isReference: true)).Y, 0.1);

        // "≤" is drawn without text: the paragraph is still found, and the symbol gets a thin caret before "1.0%".
        var caret = Assert.Single(locator.Locate(Text(4, 28, 1), isReference: true));
        Assert.Equal(170, caret.Y, 0.1);
        Assert.InRange(caret.Width, 1, 3);

        // Hidden text is not on the page, so it has no box.
        Assert.Empty(locator.Locate(Text(5, 0, 4), isReference: true));
        Assert.Equal(5, locator.LocatedParagraphCount);

        // A paragraph that exists only in the other document is shown as a line above the paragraph it precedes.
        var added = new DocumentDifference("ADD", DifferenceCategory.ParagraphAdded, "Paragraph", "(no corresponding content)", "New", null, new(9, 0),
            null, null, "", CounterpartParagraphIndex: 3);
        var gap = locator.Gap(added, isReference: true);
        Assert.NotNull(gap);
        Assert.InRange(gap!.Y, 130, 140);
    }

    [LibreOfficeFact]
    [Trait("TC", "TC-EXV-03")]
    public void Sample_documents_are_laid_out_and_every_visible_difference_is_marked()
    {
        var samples = FindSamples();
        var referencePath = Path.Combine(samples, "Sample-Reference-RevA.docx");
        var currentPath = Path.Combine(samples, "Sample-Current-RevB.docx");
        var result = new DocumentComparisonService(new NoImageText()).Compare(referencePath, currentPath);
        // Tests never open Microsoft Word on the tester's computer: LibreOffice only.
        Environment.SetEnvironmentVariable("DOCHECK_LAYOUT_ENGINE", "libreoffice");

        var (reference, referenceError) = ExactDocument.Load(referencePath, result.ReferenceDocument!, _directory, LayoutEngine.LibreOffice);
        var (current, currentError) = ExactDocument.Load(currentPath, result.CurrentDocument!, _directory, LayoutEngine.LibreOffice);

        Assert.True(reference is not null, referenceError);
        Assert.True(current is not null, currentError);
        Assert.Equal("LibreOffice", reference!.Engine);
        Assert.Equal("LibreOffice", current!.Engine);
        Assert.InRange(reference.Pages.Count, 2, 4);
        // Every difference has a mark on each side where it exists, except text hidden in Current (it is not printed).
        foreach (var difference in result.Differences)
        {
            if (difference.ReferenceLocation is not null)
                Assert.True(reference.Locator.Locate(difference, isReference: true).Count > 0, $"{difference.Id} {difference.ReferenceValue} not marked in Reference");
            if (difference.CurrentLocation is not null && difference.Property != "Hidden")
                Assert.True(current!.Locator.Locate(difference, isReference: false).Count > 0, $"{difference.Id} {difference.CurrentValue} not marked in Current");
        }
        var hidden = result.Differences.Single(difference => difference.Property == "Hidden");
        Assert.Empty(current!.Locator.Locate(hidden, isReference: false));
        // The Assay value is marked on the first page, inside the specification table; the image change on a later page.
        var assay = Assert.Single(reference.Locator.Locate(result.Differences.Single(d => d.ReferenceValue == "95.0%"), isReference: true));
        Assert.Equal(0, assay.Page);
        var image = Assert.Single(reference.Locator.Locate(result.Differences.Single(d => d.Category == DifferenceCategory.Image), isReference: true));
        Assert.True(image.Page > 0);
        // The paragraph added in Current is shown as a gap line in the approved document.
        Assert.NotNull(reference.Locator.Gap(result.Differences.Single(d => d.Category == DifferenceCategory.ParagraphAdded), isReference: true));
    }

    [Fact]
    [Trait("TC", "TC-EXV-04")]
    public void The_chosen_layout_engine_is_used_or_its_problem_is_reported_without_switching_engines()
    {
        Directory.CreateDirectory(_directory);
        var reference = Path.Combine(FindSamples(), "Sample-Reference-RevA.docx");
        // Word is switched off for test runs, so choosing it must fail with the reason, never quietly use LibreOffice.
        Environment.SetEnvironmentVariable("DOCHECK_LAYOUT_ENGINE", "libreoffice");

        var word = PageLayoutConverter.Convert(reference, _directory, LayoutEngine.Word);

        Assert.Null(word.Pdf);
        Assert.Null(word.Engine);
        Assert.Contains("switched off", word.Error);
        Assert.Equal("Microsoft Word", PageLayoutConverter.NameOf(LayoutEngine.Word));
        Assert.Equal("LibreOffice", PageLayoutConverter.NameOf(LayoutEngine.LibreOffice));
        // The temporary copy is removed again; the original is untouched.
        Assert.Empty(Directory.EnumerateFiles(_directory));

        // The choice is remembered between sessions; settings saved before the choice existed mean Word.
        var settings = Path.Combine(_directory, "settings.json");
        Assert.Equal("Word", AppSettings.Load(settings).LayoutEngine);
        new AppSettings(LayoutEngine: "LibreOffice").Save(settings);
        Assert.Equal("LibreOffice", AppSettings.Load(settings).LayoutEngine);
        Assert.True(AppSettings.Load(settings).CompareHeadersAndFooters);
        File.WriteAllText(settings, "{ \"CompareHeadersAndFooters\": false }");
        Assert.Equal(new AppSettings(false, "Word"), AppSettings.Load(settings));
    }

    /// <summary>
    /// The "Laid out by" switch in Exact pages, inside the shared UI test (Avalonia can only be set up once per
    /// process). Word is switched off for test runs, so choosing it shows the reason and offers LibreOffice;
    /// choosing LibreOffice lays the pages out again and marks the differences on them.
    /// </summary>
    internal static void VerifyEngineSwitch()
    {
        var samples = FindSamples();
        var window = new MainWindow();
        window.Show();
        try
        {
            Set(window, "_referencePath", Path.Combine(samples, "Sample-Reference-RevA.docx"));
            Set(window, "_currentPath", Path.Combine(samples, "Sample-Current-RevB.docx"));
            var comparison = (Task)Invoke(window, "RunComparisonAsync", false)!;
            WaitUntil(() => comparison.IsCompleted);
            var engineSwitch = window.FindControl<StackPanel>("EngineSwitch")!;
            var word = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("WordEngineButton")!;
            var libreOffice = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("LibreOfficeEngineButton")!;
            var referencePanel = window.FindControl<StackPanel>("ReferenceExactPanel")!;
            Assert.False(engineSwitch.IsVisible);   // only in Exact pages

            // Word is the default choice; it cannot be used here, so the reason and the other choice are shown.
            Invoke(window, "SetExactMode", true);
            Assert.True(engineSwitch.IsVisible);
            Assert.True(word.IsChecked);
            WaitUntil(() => Texts(referencePanel).Any(text => text.Contains("switched off")));
            Assert.Null(Get(window, "_referenceExact"));
            var useLibreOffice = referencePanel.GetVisualDescendants().OfType<Button>().Single();
            Assert.Equal("Use LibreOffice instead", useLibreOffice.Content);
            Capture(window, "word-unavailable");

            useLibreOffice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => Get(window, "_referenceExact") is ExactDocument && Get(window, "_currentExact") is ExactDocument &&
                            ((Dictionary<string, Control>)Get(window, "_referenceExactAnchors")).Count > 0);
            Assert.Equal("LibreOffice", ((ExactDocument)Get(window, "_referenceExact")!).Engine);
            Assert.True(libreOffice.IsChecked);
            Assert.False(word.IsChecked);
            Assert.Contains(Texts(referencePanel), text => text.Contains("laid out by LibreOffice"));
            Assert.True(((Dictionary<string, Control>)Get(window, "_currentExactAnchors")).Count > 0);
            Assert.Equal("LibreOffice", AppSettings.Load().LayoutEngine);
            Capture(window, "libreoffice");

            // Back to Word: the LibreOffice pages are not kept on screen under the Word label.
            Invoke(window, "SetLayoutEngine", LayoutEngine.Word);
            WaitUntil(() => Texts(referencePanel).Any(text => text.Contains("switched off")));
            Assert.Null(Get(window, "_referenceExact"));
            Assert.Equal("Word", AppSettings.Load().LayoutEngine);

            // And LibreOffice again comes from the cache at once.
            Invoke(window, "SetLayoutEngine", LayoutEngine.LibreOffice);
            WaitUntil(() => Texts(referencePanel).Any(text => text.Contains("laid out by LibreOffice")), seconds: 5);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Saves a picture of the window when DOCHECK_ENGINE_CAPTURE names a file prefix (for the documentation).</summary>
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DOCHECK_ENGINE_CAPTURE") is not { Length: > 0 } prefix)
            return;
        for (var pass = 0; pass < 4; pass++)
        {
            window.Measure(new Avalonia.Size(1464, 840));
            window.Arrange(new Avalonia.Rect(0, 0, 1464, 840));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1464, 840), new Avalonia.Vector(96, 96));
        bitmap.Render((Control)window.Content!);
        bitmap.Save($"{prefix}-{name}.png");
    }

    private static List<string> Texts(Control panel) =>
        panel.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToList();

    private static void WaitUntil(Func<bool> condition, int seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the exact pages.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    private static object? Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, arguments);

    private const double CharacterWidth = 6;

    /// <summary>A page with each line's characters laid out left to right from x = 10, 6 points apart.</summary>
    private static PdfPage Page(params (string Text, double Y)[] lines)
    {
        var characters = new List<PdfCharacter>();
        foreach (var (text, y) in lines)
        {
            var x = 10d;
            foreach (var character in text)
            {
                characters.Add(new PdfCharacter(character, x, y, char.IsWhiteSpace(character) ? 0 : CharacterWidth, 10));
                if (!char.IsWhiteSpace(character))
                    x += CharacterWidth;
            }
        }
        return new PdfPage(595, 842, 1, 1, new byte[4], characters, []);
    }

    private static DocumentModel Model(params (string Text, bool InHeader)[] paragraphs)
    {
        var header = new DocumentPart(DocumentPartKind.Header, "Header:0:default", "Header");
        var format = new RunFormatting(null, null, null, null, null, null, null, null, null, null, null);
        return new DocumentModel("test.docx", paragraphs
            .Select((paragraph, index) => new ParagraphBlock(index, [new TextRun(0, paragraph.Text, format)], Part: paragraph.InHeader ? header : null))
            .ToList());
    }

    private static string FindSamples()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "samples", "Sample-Reference-RevA.docx")))
                return Path.Combine(directory.FullName, "samples");
        throw new DirectoryNotFoundException("samples folder not found");
    }

    private sealed class NoImageText : IImageTextReader
    {
        public ImageTextResult Read(byte[] imageData) => new([], "OCR not used in this test.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
