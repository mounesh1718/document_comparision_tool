using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;
using M = DocumentFormat.OpenXml.Math;

namespace DocumentChecker.Engine.Tests;

/// <summary>Content that used to be missed or misreported: hidden text, symbols, equations, content controls, defaults, images, locations.</summary>
public sealed class ContentCoverageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Coverage.{Guid.NewGuid():N}");

    [Fact]
    [Trait("TC", "TC-FMT-06")]
    public void Text_hidden_in_current_is_reported()
    {
        var reference = Create("hidden-reference.docx", body => body.Append(new Paragraph(new Run(new Text("Keep out of reach of children.")))));
        var current = Create("hidden-current.docx", body => body.Append(new Paragraph(
            new Run(new RunProperties(new Vanish()), new Text("Keep out of reach of children.")))));

        var difference = Assert.Single(new DocumentComparisonService().Compare(reference, current).Differences);

        Assert.Equal("Hidden", difference.Property);
        Assert.Equal("Off", difference.ReferenceValue);
        Assert.Equal("On", difference.CurrentValue);
        Assert.Equal("Text hidden in Current", FindingText.Operation(difference));
    }

    [Fact]
    [Trait("TC", "TC-TXT-06")]
    public void Symbol_font_characters_are_read_and_compared_as_text()
    {
        Paragraph Limit(string code) => new(new Run(new Text("Total impurities ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new SymbolChar { Font = "Symbol", Char = code }), new Run(new Text(" 1.0%") { Space = SpaceProcessingModeValues.Preserve }));
        var reference = Create("symbol-reference.docx", body => body.Append(Limit("F0A3")));
        var current = Create("symbol-current.docx", body => body.Append(Limit("F0B3")));

        var result = new DocumentComparisonService().Compare(reference, current);

        Assert.Equal("Total impurities ≤ 1.0%", result.ReferenceDocument!.Paragraphs.Single().Text);
        var difference = Assert.Single(result.Differences);
        Assert.Equal("≤", difference.ReferenceValue);
        Assert.Equal("≥", difference.CurrentValue);
    }

    [Theory]
    [Trait("TC", "TC-TXT-06")]
    [InlineData("Symbol", "F020", ' ')]
    [InlineData("Symbol", "F06D", 'μ')]
    [InlineData("Symbol", "F07E", '∼')]
    [InlineData("Symbol", "F0A0", '€')]
    [InlineData("Symbol", "F0B0", '°')]
    [InlineData("Symbol", "F0B1", '±')]
    [InlineData("Symbol", "F0D6", '√')]
    [InlineData("Symbol", "F0FE", '⎭')]
    [InlineData("Symbol", "B3", '≥')]
    [InlineData("Wingdings", "F0FC", '')]
    public void Symbol_codes_map_to_their_characters(string font, string code, char expected) =>
        Assert.Equal(expected, SymbolText.ToChar(font, code));

    [Fact]
    [Trait("TC", "TC-RV-01")]
    public void An_equation_is_reported_for_review_instead_of_being_skipped()
    {
        Paragraph Equation() => new(new Run(new Text("Dose: ") { Space = SpaceProcessingModeValues.Preserve }),
            new M.OfficeMath(new M.Run(new M.Text("x=a/b"))));
        var reference = Create("equation-reference.docx", body => body.Append(Equation()));
        var current = Create("equation-current.docx", body => body.Append(Equation()));

        var result = new DocumentComparisonService().Compare(reference, current);

        Assert.NotEqual(ComparisonStatus.DocumentDataMatch, result.Status);
        Assert.Contains(result.Differences, d => d.Category == DifferenceCategory.Review && d.ReviewNote!.Contains("equation"));
    }

    [Fact]
    [Trait("TC", "TC-RD-05")]
    public void Text_in_an_inline_content_control_is_compared_without_a_review_item()
    {
        Paragraph Batch(string size) => new(new Run(new Text("Batch size: ") { Space = SpaceProcessingModeValues.Preserve }),
            new SdtRun(new SdtProperties(new SdtAlias { Val = "Batch size" }), new SdtContentRun(new Run(new Text(size)))));
        var reference = Create("control-reference.docx", body => body.Append(Batch("100 tablets")));
        var current = Create("control-current.docx", body => body.Append(Batch("200 tablets")));

        var result = new DocumentComparisonService().Compare(reference, current);

        var difference = Assert.Single(result.Differences);
        Assert.Equal("100", difference.ReferenceValue);
        Assert.Equal("200", difference.CurrentValue);
    }

    [Fact]
    [Trait("TC", "TC-FMT-07")]
    public void Explicit_default_formatting_is_not_reported_as_a_change()
    {
        var reference = Create("defaults-reference.docx", body => body.Append(new Paragraph(new Run(new Text("Same text")))));
        var current = Create("defaults-current.docx", body => body.Append(new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Start }),
            new Run(new RunProperties(new Color { Val = "auto" }, new Highlight { Val = HighlightColorValues.None },
                new Underline { Val = UnderlineValues.None }), new Text("Same text")))));

        var result = new DocumentComparisonService().Compare(reference, current);

        Assert.Empty(result.Differences);
        Assert.Equal(ComparisonStatus.DocumentDataMatch, result.Status);
    }

    [Fact]
    [Trait("TC", "TC-TBL-08")]
    public void A_deleted_row_is_reported_once_per_value_without_a_separate_row_count()
    {
        Table Spec(bool withWater) => new(new[] { Row("Test", "Limit"), Row("Assay", "95-105%") }
            .Concat(withWater ? new[] { Row("Water", "NMT 2.0%") } : Array.Empty<TableRow>())
            .Append(Row("Impurities", "NMT 0.2%")));
        var reference = Create("rows-reference.docx", body => body.Append(Spec(withWater: true)));
        var current = Create("rows-current.docx", body => body.Append(Spec(withWater: false)));

        var result = new DocumentComparisonService().Compare(reference, current);

        Assert.DoesNotContain(result.Differences, d => d.Property == "Rows");
        Assert.Equal(["Water", "NMT 2.0%"], result.Differences.Select(d => d.ReferenceValue));
        Assert.All(result.Differences, d => Assert.Equal("Row missing from Current", FindingText.Operation(d)));
    }

    [Fact]
    [Trait("TC", "TC-IMG-08")]
    public void One_changed_value_inside_an_image_is_one_visual_finding()
    {
        var reference = Create("image-reference.docx", body => body.Append(ImageParagraph(body, "Assay 99.1")));
        var current = Create("image-current.docx", body => body.Append(ImageParagraph(body, "Assay 98.4")));

        var result = new DocumentComparisonService(new NoImageText()).Compare(reference, current);

        var image = Assert.Single(result.Differences);
        Assert.Equal("Visual image change", image.Property);
        Assert.NotNull(image.ImageRegion);
    }

    [Fact]
    [Trait("TC", "TC-LOC-01")]
    public void Locations_name_the_part_and_count_paragraphs_as_a_reader_does()
    {
        var path = Create("locations.docx", body =>
        {
            body.Append(new Paragraph(new Run(new Text("Introduction"))));
            body.Append(new Table(Row("Item", "Qty"), Row("Sensor", "10")));
            body.Append(new Paragraph(new Run(new Text("After the table"))));
        }, headerText: "ACME");
        var document = new DocumentReader().Read(path).Document!;
        int IndexOf(string text) => document.Paragraphs.Single(p => p.Text == text).Index;

        // Table cells are not counted: the paragraph after the table is the second paragraph of the body.
        Assert.Equal("Page unavailable, Paragraph 2", FindingText.Location(document, new DocumentLocation(IndexOf("After the table"), 0)));
        Assert.Equal("Header, Paragraph 1", FindingText.Location(document, new DocumentLocation(IndexOf("ACME"), 0)));
        Assert.Equal("Page unavailable · Table 1, Row 2, Column 2 (Sensor · Qty)", FindingText.Location(document, new DocumentLocation(IndexOf("10"), 0)));
        Assert.Equal(FindingText.NotPresent, FindingText.Location(document, null));
    }

    [Theory]
    [Trait("TC", "TC-RPT-03")]
    [InlineData(" ", "[space]")]
    [InlineData("  ", "[space][space]")]
    [InlineData("\t", "[tab]")]
    [InlineData("", "(empty)")]
    [InlineData("(no corresponding content)", "Not present")]
    [InlineData("30", "30")]
    public void Report_values_make_whitespace_and_absent_content_visible(string value, string printed) =>
        Assert.Equal(printed, FindingText.PrintableValue(value));

    private static TableRow Row(params string[] cells) => new(cells.Select(text => new TableCell(new Paragraph(new Run(new Text(text))))));

    private static Paragraph ImageParagraph(Body body, string text)
    {
        var main = body.Ancestors<Document>().First().MainDocumentPart!;
        var part = main.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(ContentDifferenceTests.CreateTextImage(text)))
            part.FeedData(stream);
        return new Paragraph(new Run(new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 6000000, Cy = 900000 },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1, Name = "Value" },
            new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(
                new DocumentFormat.OpenXml.Drawing.Pictures.Picture(new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                    new DocumentFormat.OpenXml.Drawing.Blip { Embed = main.GetIdOfPart(part) }))))))));
    }

    private string Create(string name, Action<Body> add, string? headerText = null)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        add(body);
        if (headerText is not null)
        {
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(new Run(new Text(headerText))));
            body.Append(new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) }));
        }
        main.Document.Save();
        return path;
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
