using DocumentChecker.Engine.Comparison;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using P = DocumentFormat.OpenXml.Drawing.Pictures;
using Xunit;

namespace DocumentChecker.Engine.Tests;

public sealed class MixedComparisonTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DocCheck.Mixed." + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Image_and_table_values_share_one_change_with_real_coordinates_and_cell_location(bool imageIsReference)
    {
        var image = ImageDocument("Image.docx", "Quantity 30");
        var table = TableDocument("Table.docx", [["Quantity", "45"]]);
        var reader = new FixedReader(new([new("Quantity", new(20, 20, 90, 20)), new("30", new(130, 20, 30, 20))]));
        var result = new DocumentComparisonService(reader).Compare(imageIsReference ? image : table, imageIsReference ? table : image);
        var change = Assert.Single(result.Differences, d => d.IsRecognizedText);
        Assert.Equal(imageIsReference ? "30" : "45", change.ReferenceValue);
        Assert.Equal(imageIsReference ? "45" : "30", change.CurrentValue);
        Assert.Equal(new ImageDifferenceRegion(130, 20, 30, 20), change.RegionFor(imageIsReference));
        var tableRange = imageIsReference ? change.CurrentRange : change.ReferenceRange;
        var tableLocation = imageIsReference ? change.CurrentLocation : change.ReferenceLocation;
        Assert.Equal(2, tableRange!.Length);
        Assert.Equal(1, tableLocation!.TableCell!.CellIndex);
        Assert.Equal(imageIsReference ? DifferenceContentKind.Image : DifferenceContentKind.Table, change.ReferenceContentKind);
        Assert.Equal(imageIsReference ? DifferenceContentKind.Table : DifferenceContentKind.Image, change.CurrentContentKind);
        Assert.NotNull(change.ReviewNote);
        Assert.DoesNotContain(result.Differences, d => d.Property is "Image content" or "Table");
    }

    [Fact]
    public void Matching_recognized_data_is_still_marked_for_review_and_representation_change()
    {
        var result = new DocumentComparisonService(new FixedReader(new([new("Quantity", new(0, 0, 90, 20)), new("30", new(120, 0, 30, 20))])))
            .Compare(ImageDocument("Image.docx", "Quantity 30"), TableDocument("Table.docx", [["Quantity", "30"]]));
        Assert.Equal(ComparisonStatus.Different, result.Status);
        var difference = Assert.Single(result.Differences);
        Assert.Contains("Recognized words match", difference.NearbyText);
        Assert.NotNull(difference.ReviewNote);
    }

    [Fact]
    public void Failed_OCR_never_invents_missing_or_matching_values()
    {
        var result = new DocumentComparisonService(new FixedReader(new([], "OCR unavailable")))
            .Compare(ImageDocument("Image.docx", "Quantity 30"), TableDocument("Table.docx", [["Quantity", "45"]]));
        Assert.All(result.Differences, d => Assert.False(d.IsRecognizedText));
        Assert.Contains(result.Differences, d => d.ReviewNote == "OCR unavailable");
        Assert.NotEqual(ComparisonStatus.DocumentDataMatch, result.Status);
    }

    [Fact]
    public void Column_major_OCR_is_mapped_by_row_label_and_spatial_columns()
    {
        var ocr = new ImageTextResult([
            new("Sensor", new(10, 20, 60, 20)), new("Motor", new(10, 60, 50, 20)),
            new("10", new(200, 20, 20, 20)), new("30", new(200, 60, 20, 20))]);
        var result = new DocumentComparisonService(new FixedReader(ocr)).Compare(
            ImageDocument("Image.docx", "Sensor 10 Motor 30"), TableDocument("Table.docx", [["Sensor", "15"], ["Motor", "30"]]));
        var value = Assert.Single(result.Differences, d => d.IsRecognizedText);
        Assert.Equal("10", value.ReferenceValue);
        Assert.Equal("15", value.CurrentValue);
        Assert.Equal(new ImageDifferenceRegion(200, 20, 20, 20), value.ImageRegion);
        Assert.Equal(0, value.CurrentLocation!.TableCell!.RowIndex);
    }

    [Fact]
    public void Ambiguous_image_grid_requires_review_instead_of_pairing_unrelated_values()
    {
        var result = new DocumentComparisonService(new FixedReader(new([new("Unreadable", new(10, 20, 50, 20))])))
            .Compare(ImageDocument("Image.docx", "Unreadable"), TableDocument("Table.docx", [["Sensor", "15"], ["Motor", "30"]]));
        Assert.DoesNotContain(result.Differences, d => d.IsRecognizedText);
        Assert.Contains(result.Differences, d => d.ReviewNote!.Contains("Could not reliably align"));
    }

    [Fact]
    public void Different_named_sections_are_not_paired()
    {
        var result = new DocumentComparisonService(new FixedReader(new([])))
            .Compare(ImageDocument("Image.docx", "Quantity 30", "Inventory"), TableDocument("Table.docx", [["Quantity", "45"]], "Payments"));
        Assert.DoesNotContain(result.Differences, d => d.Id.StartsWith("MIX-"));
    }

    [Fact]
    public void Missing_middle_row_does_not_shift_subsequent_values()
    {
        var reference = TableDocument("Reference.docx", [["Item", "Qty"], ["Sensor", "10"], ["Cable", "20"], ["Motor", "30"]]);
        var current = TableDocument("Current.docx", [["Item", "Qty"], ["Sensor", "10"], ["Motor", "30"]]);
        var result = new DocumentComparisonService().Compare(reference, current);
        Assert.Contains(result.Differences, d => d.Property == "Missing row value" && d.ReferenceValue == "Cable");
        Assert.Contains(result.Differences, d => d.Property == "Missing row value" && d.ReferenceValue == "20");
        Assert.DoesNotContain(result.Differences, d => d.Category == DifferenceCategory.Text);
        Assert.DoesNotContain(result.Differences, d => d.ReferenceValue == "Motor" || d.ReferenceValue == "30");
    }

    [Fact]
    public void Missing_middle_column_does_not_shift_remaining_columns()
    {
        var reference = TableDocument("Reference.docx", [["Item", "Qty", "Price"], ["Sensor", "10", "120"]]);
        var current = TableDocument("Current.docx", [["Item", "Price"], ["Sensor", "120"]]);
        var result = new DocumentComparisonService().Compare(reference, current);
        Assert.Contains(result.Differences, d => d.Property == "Missing column value" && d.ReferenceValue == "Qty");
        Assert.Contains(result.Differences, d => d.Property == "Missing column value" && d.ReferenceValue == "10");
        Assert.DoesNotContain(result.Differences, d => d.Category == DifferenceCategory.Text);
    }

    [Fact]
    public void Underline_and_highlight_values_remain_distinct_and_readable()
    {
        var reference = Create("Reference.docx", (main, body) => body.Append(new Paragraph(new Run(
            new RunProperties(new Underline { Val = UnderlineValues.Single }, new Highlight { Val = HighlightColorValues.Yellow }), new Text("Value")))));
        var current = Create("Current.docx", (main, body) => body.Append(new Paragraph(new Run(
            new RunProperties(new Underline { Val = UnderlineValues.Double }, new Highlight { Val = HighlightColorValues.Green }), new Text("Value")))));
        var result = new DocumentComparisonService().Compare(reference, current);
        Assert.Contains(result.Differences, d => d.Property == "Underline" && d.ReferenceValue == "single" && d.CurrentValue == "double");
        Assert.Contains(result.Differences, d => d.ReferenceValue == "yellow" && d.CurrentValue == "green");
    }

    [Fact]
    public void Actual_Windows_OCR_compares_an_image_value_to_an_editable_table()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = new DocumentComparisonService().Compare(ImageDocument("Image.docx", "Quantity 30"), TableDocument("Table.docx", [["Quantity", "45"]]));
        Assert.Contains(result.Differences, d => d.IsRecognizedText && d.ReferenceValue == "30" && d.CurrentValue == "45" && d.CurrentRange is not null);
    }

    private string TableDocument(string name, string[][] rows, string heading = "Inventory") => Create(name, (main, body) =>
    {
        body.Append(Heading(heading));
        body.Append(new Table(rows.Select(row => new TableRow(row.Select(value => new TableCell(new Paragraph(new Run(new Text(value)))))))));
    });

    private string ImageDocument(string name, string text, string heading = "Inventory") => Create(name, (main, body) =>
    {
        var part = main.AddImagePart(ImagePartType.Png);
        using var stream = new MemoryStream(ContentDifferenceTests.CreateTextImage(text));
        part.FeedData(stream);
        body.Append(Heading(heading));
        body.Append(new Paragraph(new Run(new Drawing(new DW.Inline(new DW.Extent { Cx = 6000000, Cy = 900000 },
            new A.Graphic(new A.GraphicData(new P.Picture(new P.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) })))))))));
    });

    private static Paragraph Heading(string text) => new(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new Text(text)));
    private string Create(string name, Action<MainDocumentPart, Body> fill)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var part = document.AddMainDocumentPart();
        var body = new Body(); part.Document = new Document(body);
        fill(part, body); part.Document.Save();
        return path;
    }
    private sealed class FixedReader(ImageTextResult result) : IImageTextReader { public ImageTextResult Read(byte[] bytes) => result; }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
