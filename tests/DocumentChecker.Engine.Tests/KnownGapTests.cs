using DocumentChecker.Engine.Comparison;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using P = DocumentFormat.OpenXml.Drawing.Pictures;

namespace DocumentChecker.Engine.Tests;

/// <summary>
/// The correct behaviour for known gaps (TC-GAP-xx in docs/TEST_CASES.md). Each test fails today, so it is
/// skipped and listed as "Known gap" in every run. Run them with DOCHECK_RUN_KNOWN_GAPS=1; when one passes,
/// its gap is fixed: change its attribute to [Fact] and update the documents.
/// </summary>
public sealed class KnownGapTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Gaps.{Guid.NewGuid():N}");

    [KnownGapFact("TC-GAP-04", "list numbering is not compared")]
    [Trait("TC", "TC-GAP-04")]
    public void Removed_list_numbering_is_reported()
    {
        Paragraph Step(bool numbered) => numbered
            ? new Paragraph(new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
                new Run(new Text("Weigh the sample.")))
            : new Paragraph(new Run(new Text("Weigh the sample.")));
        var result = Compare(body => body.Append(Step(numbered: true)), body => body.Append(Step(numbered: false)));

        Assert.NotEmpty(result.Differences);
    }

    [KnownGapFact("TC-GAP-05", "character styles are not compared")]
    [Trait("TC", "TC-GAP-05")]
    public void Changed_character_style_is_reported()
    {
        Paragraph Warning(string? style) => new(style is null
            ? new Run(new Text("Warning"))
            : new Run(new RunProperties(new RunStyle { Val = style }), new Text("Warning")));
        var result = Compare(body => body.Append(Warning("Strong")), body => body.Append(Warning(null)));

        Assert.NotEmpty(result.Differences);
    }

    [KnownGapFact("TC-GAP-08", "findings are grouped by type instead of following the document")]
    [Trait("TC", "TC-GAP-08")]
    public void Findings_follow_document_reading_order()
    {
        Table Values(string value) => new(new TableRow(new TableCell(new Paragraph(new Run(new Text("Assay")))),
            new TableCell(new Paragraph(new Run(new Text(value))))));
        var result = Compare(
            body => { body.Append(Values("98")); body.Append(new Paragraph(new Run(new Text("Store below 25 C")))); },
            body => { body.Append(Values("97")); body.Append(new Paragraph(new Run(new Text("Store below 30 C")))); });

        // The table comes first in the document, so its change must be listed first.
        Assert.Equal("98", result.Differences[0].ReferenceValue);
    }

    [KnownGapFact("TC-GAP-10", "one formatting change over several Word runs gives one finding per run")]
    [Trait("TC", "TC-GAP-10")]
    public void Bold_removed_from_a_word_stored_in_several_runs_is_one_finding()
    {
        var result = Compare(
            body => body.Append(new Paragraph(new Run(new RunProperties(new Bold()), new Text("Important")))),
            body => body.Append(new Paragraph(new Run(new Text("Imp")), new Run(new Text("ort")), new Run(new Text("ant")))));

        Assert.Single(result.Differences, d => d.Property == "Bold");
    }

    [KnownGapFact("TC-GAP-13", "edited text next to an image-to-table swap is reported as deleted")]
    [Trait("TC", "TC-GAP-13")]
    public void Edited_note_next_to_an_image_to_table_swap_is_reported_as_changed()
    {
        var ocr = new FixedReader(new([new("Assay", new(10, 20, 60, 20)), new("99", new(200, 20, 25, 20))]));
        var result = Compare(
            (main, body) =>
            {
                body.Append(Heading("Results"));
                body.Append(ImageParagraph(main));
                body.Append(new Paragraph(new Run(new Text("Tested at 25 C"))));
            },
            (main, body) =>
            {
                body.Append(Heading("Results"));
                body.Append(new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("Assay")))),
                    new TableCell(new Paragraph(new Run(new Text("99")))))));
                body.Append(new Paragraph(new Run(new Text("Tested at 30 C"))));
            }, ocr);

        Assert.Contains(result.Differences, d => d.ReferenceValue == "25" && d.CurrentValue == "30");
        Assert.DoesNotContain(result.Differences, d => d.Category == DifferenceCategory.ParagraphRemoved);
    }

    [KnownGapFact("TC-GAP-14", "paragraphs inside one table cell are paired by position")]
    [Trait("TC", "TC-GAP-14")]
    public void Line_inserted_at_the_top_of_a_cell_is_one_added_paragraph()
    {
        Table Cell(params string[] lines) => new(new TableRow(new TableCell(lines.Select(line => new Paragraph(new Run(new Text(line)))))));
        var result = Compare(body => body.Append(Cell("Line A", "Line B")), body => body.Append(Cell("New", "Line A", "Line B")));

        var added = Assert.Single(result.Differences);
        Assert.Equal(DifferenceCategory.ParagraphAdded, added.Category);
        Assert.Equal("New", added.CurrentValue);
    }

    private ComparisonResult Compare(Action<Body> reference, Action<Body> current) =>
        Compare((_, body) => reference(body), (_, body) => current(body));

    private ComparisonResult Compare(Action<MainDocumentPart, Body> reference, Action<MainDocumentPart, Body> current, IImageTextReader? reader = null)
    {
        Directory.CreateDirectory(_directory);
        return new DocumentComparisonService(reader ?? new FixedReader(new([], "OCR not used in this test.")))
            .Compare(Create("reference.docx", reference), Create("current.docx", current));
    }

    private string Create(string name, Action<MainDocumentPart, Body> fill)
    {
        var path = Path.Combine(_directory, name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        fill(main, body);
        main.Document.Save();
        return path;
    }

    private static Paragraph Heading(string text) => new(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new Text(text)));

    private static Paragraph ImageParagraph(MainDocumentPart main)
    {
        var part = main.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(ContentDifferenceTests.CreateTextImage("Assay 99")))
            part.FeedData(stream);
        return new Paragraph(new Run(new Drawing(new DW.Inline(new DW.Extent { Cx = 6000000, Cy = 900000 },
            new A.Graphic(new A.GraphicData(new P.Picture(new P.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }))))))));
    }

    private sealed class FixedReader(ImageTextResult result) : IImageTextReader
    {
        public ImageTextResult Read(byte[] imageData) => result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
