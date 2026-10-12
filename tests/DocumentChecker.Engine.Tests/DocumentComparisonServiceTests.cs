using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace DocumentChecker.Engine.Tests;

public sealed class DocumentComparisonServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Tests.{Guid.NewGuid():N}");

    [Fact]
    [Trait("TC", "TC-TXT-01")]
    [Trait("TC", "TC-FMT-02")]
    public void Compare_finds_paired_text_and_subscript_formatting_differences()
    {
        var reference = CreateDocument("reference.docx", body =>
        {
            body.Append(new Paragraph(new Run(new Text("Payment within 30 days"))));
            body.Append(new Paragraph(
                new Run(new Text("CO")),
                new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript }), new Text("2"))));
        });
        var current = CreateDocument("current.docx", body =>
        {
            body.Append(new Paragraph(new Run(new Text("Payment within 45 days"))));
            body.Append(new Paragraph(new Run(new Text("CO")), new Run(new Text("2"))));
        });

        var result = new DocumentComparisonService().Compare(reference, current);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        var text = Assert.Single(result.Differences, item => item.Category == DifferenceCategory.Text);
        Assert.Equal("30", text.ReferenceValue);
        Assert.Equal("45", text.CurrentValue);
        Assert.NotNull(text.ReferenceRange);
        Assert.NotNull(text.CurrentRange);
        var formatting = Assert.Single(result.Differences, item => item.Property == "Subscript");
        Assert.Equal("On", formatting.ReferenceValue);
        Assert.Equal("Off", formatting.CurrentValue);
    }

    [Fact]
    [Trait("TC", "TC-FMT-01")]
    public void Merged_runs_do_not_hide_formula_subscripts_in_body_or_tables()
    {
        Paragraph Formula(bool subscript) => subscript
            ? new Paragraph(new Run(new Text("Water: H")), new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript }), new Text("2")), new Run(new Text("O")))
            : new Paragraph(new Run(new Text("Water: H2O")));
        var reference = CreateDocument("reference-formulas.docx", body => { body.Append(Formula(true)); body.Append(new Table(new TableRow(new TableCell(Formula(true))))); });
        var current = CreateDocument("current-formulas.docx", body => { body.Append(Formula(false)); body.Append(new Table(new TableRow(new TableCell(Formula(false))))); });
        var result = new DocumentComparisonService().Compare(reference, current);
        var changes = result.Differences.Where(d => d.Property == "Subscript").ToList();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, d => { Assert.Equal("On", d.ReferenceValue); Assert.Equal("Off", d.CurrentValue); Assert.Equal(8, d.ReferenceRange!.Start); Assert.Equal(1, d.ReferenceRange.Length); Assert.Equal(d.ReferenceRange.Start, d.CurrentRange!.Start); });
        Assert.Contains(changes, d => d.CurrentLocation!.TableCell is not null);
    }

    [Fact]
    [Trait("TC", "TC-FMT-03")]
    public void Formatting_follows_text_after_insertions_and_ignores_equivalent_run_splitting()
    {
        var reference = CreateDocument("reference-runs.docx", b => b.Append(new Paragraph(new Run(new Text("AB")), new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }), new Text("2")))));
        var current = CreateDocument("current-runs.docx", b => b.Append(new Paragraph(new Run(new Text("X")), new Run(new Text("A")), new Run(new Text("B")), new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }), new Text("2")))));
        var result = new DocumentComparisonService().Compare(reference, current);
        Assert.DoesNotContain(result.Differences, d => d.Category == DifferenceCategory.Formatting);
        var plain = CreateDocument("plain-runs.docx", b => b.Append(new Paragraph(new Run(new Text("XAB2")))));
        var difference = Assert.Single(new DocumentComparisonService().Compare(reference, plain).Differences, d => d.Property == "Superscript");
        Assert.Equal(2, difference.ReferenceRange!.Start);
        Assert.Equal(3, difference.CurrentRange!.Start);
        Assert.Equal(1, difference.CurrentRange.Length);
    }

    [Fact]
    [Trait("TC", "TC-RES-01")]
    public void Compare_returns_exact_file_match_for_the_same_file()
    {
        var path = CreateDocument("same.docx", body => body.Append(new Paragraph(new Run(new Text("Same content")))));

        var result = new DocumentComparisonService().Compare(path, path);

        Assert.Equal(ComparisonStatus.ExactFileMatch, result.Status);
        Assert.Empty(result.Differences);
    }

    [Fact]
    [Trait("TC", "TC-RES-02")]
    public void Compare_ignores_package_and_metadata_differences_when_visible_content_matches()
    {
        var reference = CreateDocument("reference.docx", body => body.Append(new Paragraph(new Run(new Text("Same visible content")))));
        var current = CreateDocument("current.docx", body => body.Append(new Paragraph(new Run(new Text("Same visible content")))));

        var result = new DocumentComparisonService().Compare(reference, current);

        Assert.Equal(ComparisonStatus.DocumentDataMatch, result.Status);
        Assert.NotEqual(result.ReferenceHash, result.CurrentHash);
        Assert.Empty(result.Differences);
    }

    [Fact]
    [Trait("TC", "TC-ALN-01")]
    public void Compare_aligns_an_inserted_paragraph_without_marking_later_paragraphs_as_changed()
    {
        var reference = CreateDocument("reference.docx", body =>
        {
            body.Append(new Paragraph(new Run(new Text("A"))));
            body.Append(new Paragraph(new Run(new Text("B"))));
            body.Append(new Paragraph(new Run(new Text("C"))));
        });
        var current = CreateDocument("current.docx", body =>
        {
            body.Append(new Paragraph(new Run(new Text("A"))));
            body.Append(new Paragraph(new Run(new Text("NEW"))));
            body.Append(new Paragraph(new Run(new Text("B"))));
            body.Append(new Paragraph(new Run(new Text("C"))));
        });

        var result = new DocumentComparisonService().Compare(reference, current);

        var difference = Assert.Single(result.Differences);
        Assert.Equal(DifferenceCategory.ParagraphAdded, difference.Category);
        Assert.Equal("NEW", difference.CurrentValue);
    }

    [Fact]
    [Trait("TC", "TC-TXT-04")]
    public void Compare_detects_a_line_missing_from_the_current_document()
    {
        var reference = CreateDocument("reference.docx", body => body.Append(new Paragraph(new Run(new Text("Required line")))));
        var current = CreateDocument("current.docx", body => body.Append(new Paragraph()));

        var result = new DocumentComparisonService().Compare(reference, current);

        var difference = Assert.Single(result.Differences, item => item.Category == DifferenceCategory.Text);
        Assert.Equal("Required line", difference.ReferenceValue);
        Assert.Equal("(no corresponding content)", difference.CurrentValue);
        Assert.Equal(0, difference.CurrentRange!.Length);
    }

    [Fact]
    [Trait("TC", "TC-TXT-05")]
    public void Compare_detects_extra_or_missing_whitespace()
    {
        var reference = CreateDocument("reference-spaces.docx", body =>
            body.Append(new Paragraph(new Run(new Text("Payment due in  30 days")))));
        var current = CreateDocument("current-spaces.docx", body =>
            body.Append(new Paragraph(new Run(new Text("Payment due in 30 days")))));

        var result = new DocumentComparisonService().Compare(reference, current);

        var difference = Assert.Single(result.Differences, item => item.Category == DifferenceCategory.Text);
        Assert.Equal(" ", difference.ReferenceValue);
        Assert.Equal("(no corresponding content)", difference.CurrentValue);
    }

    [Fact]
    [Trait("TC", "TC-FMT-04")]
    public void Compare_detects_paragraph_alignment_and_spacing_changes()
    {
        var reference = CreateDocument("reference-layout.docx", body => body.Append(new Paragraph(
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { After = "120" }),
            new Run(new Text("Approved title")))));
        var current = CreateDocument("current-layout.docx", body => body.Append(new Paragraph(
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Right },
                new SpacingBetweenLines { After = "240" }),
            new Run(new Text("Approved title")))));

        var result = new DocumentComparisonService().Compare(reference, current);

        Assert.Contains(result.Differences, item => item.Property == "Paragraph alignment" &&
            item.ReferenceValue == "center" && item.CurrentValue == "right");
        Assert.Contains(result.Differences, item => item.Property == "Space after" &&
            item.ReferenceValue == "120 twips" && item.CurrentValue == "240 twips");
    }

    [Fact]
    [Trait("TC", "TC-RD-01")]
    public void Reader_extracts_table_cell_paragraphs_for_comparison()
    {
        var path = CreateDocument("table.docx", body =>
        {
            body.Append(new Table(
                new TableRow(
                    new TableCell(new Paragraph(new Run(new Text("Cell value 15")))))));
        });

        var result = new DocumentReader().Read(path);

        Assert.True(result.IsSuccess);
        var paragraph = Assert.Single(result.Document!.Paragraphs);
        Assert.Equal("Cell value 15", paragraph.Text);
    }

    [Fact]
    [Trait("TC", "TC-RD-02")]
    public void Reader_preserves_document_display_blocks_and_paragraph_layout()
    {
        var path = CreateDocument("presentation.docx", body =>
        {
            body.Append(new Paragraph(
                new ParagraphProperties(
                    new ParagraphStyleId { Val = "Heading1" },
                    new Justification { Val = JustificationValues.Center },
                    new SpacingBetweenLines { Before = "120", After = "60" }),
                new Run(new Text("Project heading"))));
            body.Append(new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("Table value")))))));
        });

        var result = new DocumentReader().Read(path);

        Assert.True(result.IsSuccess);
        var heading = Assert.IsType<ParagraphDisplayBlock>(result.Document!.DisplayBlocks[0]).Paragraph;
        Assert.Equal("Heading1", heading.Layout!.StyleId);
        Assert.Equal("center", heading.Layout.Alignment);
        var table = Assert.IsType<TableDisplayBlock>(result.Document.DisplayBlocks[1]);
        Assert.Equal("Table value", table.Rows[0].Cells[0].Paragraphs[0].Text);
    }

    [Fact]
    [Trait("TC", "TC-TBL-01")]
    public void Compare_detects_table_cell_data_and_reports_table_cell_location()
    {
        var reference = CreateDocument("reference-table.docx", body => body.Append(
            new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("10"))))))));
        var current = CreateDocument("current-table.docx", body => body.Append(
            new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("15"))))))));

        var result = new DocumentComparisonService().Compare(reference, current);

        var difference = Assert.Single(result.Differences, item => item.Category == DifferenceCategory.Text);
        Assert.Equal("10", difference.ReferenceValue);
        Assert.Equal("15", difference.CurrentValue);
        Assert.NotNull(difference.ReferenceLocation!.TableCell);
        Assert.Equal(0, difference.ReferenceLocation.TableCell!.TableIndex);
        Assert.Equal(0, difference.ReferenceLocation.TableCell.RowIndex);
        Assert.Equal(0, difference.ReferenceLocation.TableCell.CellIndex);
    }

    [Fact]
    [Trait("TC", "TC-TBL-02")]
    public void Compare_reports_when_a_table_exists_on_only_one_side()
    {
        var reference = CreateDocument("reference-table-only.docx", body => body.Append(
            new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("Table data"))))))));
        var current = CreateDocument("current-no-table.docx", body => body.Append(new Paragraph(new Run(new Text("Image or text content")))));

        var result = new DocumentComparisonService().Compare(reference, current);

        var difference = Assert.Single(result.Differences, item => item.Category == DifferenceCategory.Table);
        Assert.Equal("Table", difference.Property);
        Assert.Equal("(no corresponding table)", difference.CurrentValue);
    }

    [OcrFact]
    [Trait("TC", "TC-IMG-01")]
    public void Compare_reads_changed_words_from_embedded_images_in_docx()
    {
        var reference = CreateDocument("reference-image.docx", _ => { });
        var current = CreateDocument("current-image.docx", _ => { });
        AddImage(reference, "Payment terms: 30 days", "rIdReference");
        AddImage(current, "Payment terms: 45 days", "rIdCurrent");
        var result = new DocumentComparisonService().Compare(reference, current);

        Assert.True(result.IsSuccess);
        var difference = Assert.Single(result.Differences, item => item.IsRecognizedText);
        Assert.Equal("30", difference.ReferenceValue);
        Assert.Equal("45", difference.CurrentValue);
        Assert.NotNull(difference.ImageRegion);
        Assert.NotNull(difference.CurrentImageRegion);
        Assert.NotNull(difference.ReferenceImageId);
        Assert.DoesNotContain(result.Differences, item => item.Property is "Binary hash" or "Relationship");
        Assert.All(result.Differences, item => Assert.DoesNotContain("SHA-256", item.ReferenceValue));
    }

    private static void AddImage(string path, string text, string relationshipId)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var part = document.MainDocumentPart!;
        var imagePart = part.AddImagePart(ImagePartType.Png, relationshipId);
        using var stream = new MemoryStream(ContentDifferenceTests.CreateTextImage(text));
        imagePart.FeedData(stream);
        var drawing = new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 5000000, Cy = 800000 },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1, Name = "Payment terms" },
            new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(
                new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                    new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                        new DocumentFormat.OpenXml.Drawing.Blip { Embed = relationshipId }))))));
        part.Document!.Body!.Append(new Paragraph(new Run(drawing)));
        part.Document.Save();
    }

    [Fact]
    [Trait("TC", "TC-IN-01")]
    public void Reader_reports_a_clear_message_when_a_document_is_locked()
    {
        var path = CreateDocument("locked.docx", body => body.Append(new Paragraph(new Run(new Text("Locked")))));

        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = new DocumentReader().Read(path);

        var issue = Assert.Single(result.Issues);
        Assert.False(result.IsSuccess);
        Assert.Equal("DOCX-FILE-UNAVAILABLE", issue.Code);
        Assert.Contains("locked.docx", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("click Compare again", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private string CreateDocument(string name, Action<Body> addBodyContent)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var path = Path.Combine(_temporaryDirectory, name);
        using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var body = new Body();
        mainPart.Document = new Document(body);
        addBodyContent(body);
        mainPart.Document.Save();
        return path;
    }
}
