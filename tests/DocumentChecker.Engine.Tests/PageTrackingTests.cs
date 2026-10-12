using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace DocumentChecker.Engine.Tests;

public sealed class PageTrackingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Pages.{Guid.NewGuid():N}");

    [Fact]
    [Trait("TC", "TC-PG-01")]
    public void Changed_text_uses_its_position_within_a_paragraph_and_separate_pages_on_each_side()
    {
        var reference = Create("reference.docx", body => body.Append(new Paragraph(new Run(
            new Text("First "), new LastRenderedPageBreak(), new Text("Second "), new LastRenderedPageBreak(), new Text("Qty 10")))));
        var current = Create("current.docx", body => body.Append(new Paragraph(new Run(
            new Text("First Second "), new LastRenderedPageBreak(), new Text("Qty 15")))));
        var difference = Assert.Single(new DocumentComparisonService().Compare(reference, current).Differences);
        Assert.Equal(3, difference.ReferenceLocation!.Page!.Number);
        Assert.Equal(2, difference.CurrentLocation!.Page!.Number);
        Assert.Equal(PageNumberSource.SavedLayout, difference.ReferenceLocation.Page.Source);
        Assert.Contains("Page 3", difference.ReferenceLocation.Display);
    }

    [Fact]
    [Trait("TC", "TC-PG-02")]
    public void Explicit_break_and_cached_marker_are_not_counted_twice_in_a_table()
    {
        var path = Create("table.docx", body =>
        {
            body.Append(new Paragraph(new Run(new Text("First"), new Break { Type = BreakValues.Page })));
            body.Append(new Table(new TableRow(new TableCell(new Paragraph(new Run(new LastRenderedPageBreak(), new Text("15")))))));
        });
        var read = new DocumentReader().Read(path);
        var cell = Assert.Single(read.Document!.Paragraphs, paragraph => paragraph.IsInTable);
        Assert.Equal(2, cell.PageAt(0)!.Number);
    }

    [Fact]
    [Trait("TC", "TC-PG-04")]
    public void Page_break_before_and_its_saved_marker_share_one_page_boundary()
    {
        var path = Create("before.docx", body =>
        {
            body.Append(new Paragraph(new Run(new Text("First"))));
            body.Append(new Paragraph(new ParagraphProperties(new PageBreakBefore()), new Run(new LastRenderedPageBreak(), new Text("Second"))));
        });
        var read = new DocumentReader().Read(path);
        Assert.Equal(2, read.Document!.Paragraphs[1].PageAt(0)!.Number);
    }

    [Fact]
    [Trait("TC", "TC-PG-03")]
    public void Manual_breaks_without_saved_layout_are_explicitly_estimated()
    {
        var path = Create("manual.docx", body => body.Append(new Paragraph(new Run(new Text("First"),
            new Break { Type = BreakValues.Page }, new Text("Second")))));
        var paragraph = new DocumentReader().Read(path).Document!.Paragraphs[0];
        Assert.Equal(1, paragraph.PageAt(0)!.Number);
        var page = paragraph.PageAt(6)!;
        Assert.Equal(2, page.Number);
        Assert.Equal(PageNumberSource.ExplicitBreaks, page.Source);
        Assert.Contains("estimated", page.Display);
    }

    [Fact]
    [Trait("TC", "TC-PG-05")]
    public void Documents_without_page_positions_do_not_claim_an_exact_page_one()
    {
        var path = Create("unknown.docx", body => body.Append(new Paragraph(new Run(new Text("No cached layout")))));
        Assert.Null(new DocumentReader().Read(path).Document!.Paragraphs[0].PageAt(0));
        Assert.Equal("Page unavailable", new DocumentLocation(0, 0).PageDisplay);
    }

    [Fact]
    [Trait("TC", "TC-PG-06")]
    public void Embedded_image_after_an_intrarun_break_gets_its_own_page()
    {
        var path = Create("image.docx", body => body.Append(new Paragraph(new Run(new Text("Page one")))));
        using (var document = WordprocessingDocument.Open(path, true))
        {
            var main = document.MainDocumentPart!;
            var imagePart = main.AddImagePart(ImagePartType.Png, "rIdImage");
            using var image = new MemoryStream(ContentDifferenceTests.CreateTextImage("Invoice 9001"));
            imagePart.FeedData(image);
            var drawing = new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
                new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(
                    new DocumentFormat.OpenXml.Drawing.Pictures.Picture(new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                        new DocumentFormat.OpenXml.Drawing.Blip { Embed = "rIdImage" }))))));
            main.Document!.Body!.Append(new Paragraph(new Run(new LastRenderedPageBreak(), drawing)));
            main.Document.Save();
        }
        var read = new DocumentReader().Read(path);
        Assert.Equal(2, Assert.Single(read.Document!.ImageItems).Page!.Number);
    }

    private string Create(string name, Action<Body> append)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        append(body);
        main.Document.Save();
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
