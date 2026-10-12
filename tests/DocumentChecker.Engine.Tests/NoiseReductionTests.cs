using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>Content that did not change must not produce findings: merged cells, nested tables, shifted tables and images.</summary>
public sealed class NoiseReductionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Noise.{Guid.NewGuid():N}");

    [Fact]
    [Trait("TC", "TC-RD-03")]
    public void Merged_cells_are_read_with_their_grid_position_and_span()
    {
        var path = Create("merged.docx", body => body.Append(MergedTable("250 mg")));

        var document = new DocumentReader().Read(path).Document!;

        var rows = Assert.IsType<TableDisplayBlock>(document.DisplayBlocks.Single()).Rows;
        // Row 1: "Strength" spans two rows; "Composition" spans two columns.
        Assert.Equal((0, 1, 2, false), Shape(rows[0].Cells[0]));
        Assert.Equal((1, 2, 1, false), Shape(rows[0].Cells[1]));
        // Row 2: the first cell continues "Strength"; the others start at grid columns 1 and 2.
        Assert.Equal((0, 1, 1, true), Shape(rows[1].Cells[0]));
        Assert.Equal((1, 1, 1, false), Shape(rows[1].Cells[1]));
        Assert.Equal((2, 1, 1, false), Shape(rows[1].Cells[2]));
        Assert.Empty(document.ReviewNotes!);

        static (int, int, int, bool) Shape(DocumentTableCell cell) => (cell.GridColumn, cell.ColumnSpan, cell.RowSpan, cell.MergedFromAbove);
    }

    [Fact]
    [Trait("TC", "TC-TBL-05")]
    public void Identical_merged_tables_produce_no_findings_and_a_changed_value_is_found()
    {
        var reference = Create("merged-reference.docx", body => body.Append(MergedTable("250 mg")));
        var same = Create("merged-same.docx", body => body.Append(MergedTable("250 mg")));
        var changed = Create("merged-changed.docx", body => body.Append(MergedTable("500 mg")));

        Assert.Empty(new DocumentComparisonService().Compare(reference, same).Differences);
        var difference = Assert.Single(new DocumentComparisonService().Compare(reference, changed).Differences);
        Assert.Equal("250", difference.ReferenceValue);
        Assert.Equal("500", difference.CurrentValue);
    }

    [Fact]
    [Trait("TC", "TC-TBL-06")]
    public void Text_inside_a_nested_table_is_compared_without_a_review_warning()
    {
        Table Outer(string value) => new(new TableRow(new TableCell(Text("Outer"), new Table(new TableRow(new TableCell(Text(value)))), Text("After"))));
        var reference = Create("nested-reference.docx", body => body.Append(Outer("Inner 30")));
        var current = Create("nested-current.docx", body => body.Append(Outer("Inner 45")));

        var result = new DocumentComparisonService().Compare(reference, current);

        var difference = Assert.Single(result.Differences);
        Assert.Equal("30", difference.ReferenceValue);
        Assert.Equal("45", difference.CurrentValue);
        Assert.NotNull(difference.ReferenceLocation!.TableCell);
    }

    [Fact]
    [Trait("TC", "TC-TBL-07")]
    public void A_table_inserted_in_the_middle_does_not_shift_the_tables_after_it()
    {
        Table Simple(string name, string value) => new(new TableRow(new TableCell(Text(name)), new TableCell(Text(value))));
        var reference = Create("tables-reference.docx", body =>
        {
            body.Append(Simple("Assay", "98%"));
            body.Append(Simple("Dissolution", "85%"));
            body.Append(Simple("Water", "2%"));
        });
        var current = Create("tables-current.docx", body =>
        {
            body.Append(Simple("Assay", "98%"));
            body.Append(Simple("Impurities", "0.5%"));
            body.Append(Simple("Dissolution", "85%"));
            body.Append(Simple("Water", "2%"));
        });

        var result = new DocumentComparisonService().Compare(reference, current);

        var added = Assert.Single(result.Differences);
        Assert.Equal(DifferenceCategory.Table, added.Category);
        Assert.Equal("(no corresponding table)", added.ReferenceValue);
        Assert.Equal(1, added.CurrentTableIndex);
        // Dissolution and Water pair with their own tables, not with their old positions.
        Assert.Equal(2, result.CounterpartTableOf(1, isReference: true));
        Assert.Equal(3, result.CounterpartTableOf(2, isReference: true));
        Assert.Null(result.CounterpartTableOf(1, isReference: false));
    }

    [Fact]
    [Trait("TC", "TC-IMG-07")]
    public void Images_below_new_content_are_not_reported_as_moved_and_a_new_image_is_reported_as_added()
    {
        var reference = Create("images-reference.docx", body =>
        {
            body.Append(Text("Introduction"));
            body.Append(ImageParagraph(body, "Logo", "rIdA"));
        });
        var current = Create("images-current.docx", body =>
        {
            body.Append(Text("Introduction"));
            body.Append(Text("A new paragraph above the logo."));
            body.Append(Text("Another new paragraph."));
            body.Append(ImageParagraph(body, "Chart", "rIdB"));
            body.Append(ImageParagraph(body, "Logo", "rIdA"));
        });

        var result = new DocumentComparisonService(new NoImageText()).Compare(reference, current);

        Assert.DoesNotContain(result.Differences, d => d.Property == "Image placement");
        var image = Assert.Single(result.Differences, d => d.Category == DifferenceCategory.Image);
        Assert.Equal("(no corresponding image)", image.ReferenceValue);
        Assert.Equal(3, image.CurrentLocation!.ParagraphIndex);
    }

    private static Table MergedTable(string strength) => new(
        new TableRow(
            new TableCell(new TableCellProperties(new VerticalMerge { Val = MergedCellValues.Restart }), Text("Strength")),
            new TableCell(new TableCellProperties(new GridSpan { Val = 2 }), Text("Composition"))),
        new TableRow(
            new TableCell(new TableCellProperties(new VerticalMerge()), new Paragraph()),
            new TableCell(Text(strength)),
            new TableCell(Text("% w/w"))));

    private static Paragraph Text(string text) => new(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text(text)));

    /// <summary>A paragraph holding one picture; pictures with the same text have the same pixels.</summary>
    private static Paragraph ImageParagraph(Body body, string text, string relationshipId)
    {
        var main = (MainDocumentPart)body.Ancestors<Document>().First().MainDocumentPart!;
        if (main.Parts.All(part => part.RelationshipId != relationshipId))
        {
            var imagePart = main.AddImagePart(ImagePartType.Png, relationshipId);
            using var stream = new MemoryStream(ContentDifferenceTests.CreateTextImage(text));
            imagePart.FeedData(stream);
        }
        return new Paragraph(new Run(new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 900000, Cy = 300000 },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1, Name = text },
            new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(
                new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                    new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                        new DocumentFormat.OpenXml.Drawing.Blip { Embed = relationshipId })
                )))))));
    }

    private string Create(string name, Action<Body> add)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        add(body);
        main.Document.Save();
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class NoImageText : IImageTextReader
    {
        public ImageTextResult Read(byte[] imageData) => new([], "OCR not used in this test.");
    }
}
