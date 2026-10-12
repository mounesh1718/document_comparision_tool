using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.ExactView;
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

        var (reference, referenceError) = ExactDocument.Load(referencePath, result.ReferenceDocument!, _directory);
        var (current, currentError) = ExactDocument.Load(currentPath, result.CurrentDocument!, _directory);

        Assert.True(reference is not null, referenceError);
        Assert.True(current is not null, currentError);
        Assert.Equal("LibreOffice", reference!.Engine);
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
