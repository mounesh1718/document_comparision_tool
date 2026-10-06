using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>Headers, footers and notes are compared like body text, each with its own counterpart.</summary>
public sealed class HeaderFooterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Parts.{Guid.NewGuid():N}");

    [Fact]
    public void Header_footer_and_note_changes_are_found_and_located()
    {
        var reference = Create("reference.docx", "ACME Ltd — Confidential", "Approved by QA", "Rev A", footnote: "Source: 2024 audit.");
        var current = Create("current.docx", "ACME Ltd — Internal", "Approved by QA", "Rev B", footnote: "Source: 2025 audit.");

        var result = new DocumentComparisonService().Compare(reference, current);

        var header = Assert.Single(result.Differences, d => d.ReferenceValue == "Confidential");
        Assert.Equal("Internal", header.CurrentValue);
        Assert.Equal(DocumentPartKind.Header, PartOf(result.ReferenceDocument!, header.ReferenceLocation!).Kind);
        Assert.Equal("Header", PartOf(result.ReferenceDocument!, header.ReferenceLocation!).Name);

        var footer = Assert.Single(result.Differences, d => d.ReferenceValue == "A" && d.CurrentValue == "B");
        Assert.Equal(DocumentPartKind.Footer, PartOf(result.ReferenceDocument!, footer.ReferenceLocation!).Kind);

        var note = Assert.Single(result.Differences, d => d.ReferenceValue == "2024");
        Assert.Equal("Footnote 1", PartOf(result.ReferenceDocument!, note.ReferenceLocation!).Name);

        // The unchanged footer line and the body are not reported.
        Assert.DoesNotContain(result.Differences, d => d.ReferenceValue.Contains("Approved") || d.CurrentValue.Contains("Approved"));
        Assert.DoesNotContain(result.Differences, d => d.ReferenceValue.Contains("Body") || d.CurrentValue.Contains("Body"));
        Assert.DoesNotContain(result.Differences, d => d.Category == DifferenceCategory.Review);
    }

    [Fact]
    public void Page_number_fields_are_compared_as_fields_not_printed_values()
    {
        var reference = Create("reference-pages.docx", "Header", "Approved by QA", "Rev A", pageField: "1");
        var current = Create("current-pages.docx", "Header", "Approved by QA", "Rev A", pageField: "7");

        var result = new DocumentComparisonService().Compare(reference, current);

        Assert.Equal(ComparisonStatus.DocumentDataMatch, result.Status);
        var footerText = result.ReferenceDocument!.Paragraphs.Single(p => p.Part?.Kind == DocumentPartKind.Footer && p.Text.Contains("Page")).Text;
        Assert.Equal("Page [Page number] of [Page count]", footerText);
    }

    [Fact]
    public void Fields_in_the_body_keep_their_printed_value()
    {
        string Dated(string name, string date)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, name);
            using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(
                new Run(new Text("Effective: ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                new Run(new FieldCode(" DATE ")),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                new Run(new Text(date)),
                new Run(new FieldChar { FieldCharType = FieldCharValues.End }))));
            main.Document.Save();
            return path;
        }

        var result = new DocumentComparisonService().Compare(Dated("reference-date.docx", "01/01/2024"), Dated("current-date.docx", "02/02/2025"));

        var change = Assert.Single(result.Differences);
        Assert.Equal("01/01/2024", change.ReferenceValue);
        Assert.Equal("02/02/2025", change.CurrentValue);
    }

    [Fact]
    public void A_footer_present_only_in_the_reference_is_reported_as_missing()
    {
        var reference = Create("reference-footer.docx", "Header", "Approved by QA", "Rev A");
        var current = Create("current-no-footer.docx", "Header", footerLine: null, revision: null);

        var result = new DocumentComparisonService().Compare(reference, current);

        var removed = result.Differences.Where(d => d.Category == DifferenceCategory.ParagraphRemoved).ToList();
        Assert.Equal(2, removed.Count);
        Assert.All(removed, d => Assert.Equal(DocumentPartKind.Footer, PartOf(result.ReferenceDocument!, d.ReferenceLocation!).Kind));
        Assert.All(removed, d => Assert.Equal(DocumentDifference.EndOfDocument, d.CounterpartParagraphIndex));
        // Header paragraphs never pair with body paragraphs even when the texts are similar.
        Assert.DoesNotContain(result.Differences, d => d.Category == DifferenceCategory.Text);
    }

    [Fact]
    public void Header_images_are_read_from_the_header_part()
    {
        var path = Create("logo.docx", "Header", "Approved by QA", "Rev A", headerImage: true);

        var document = new DocumentReader().Read(path).Document!;

        var logo = Assert.Single(document.ImageItems);
        Assert.Equal(DocumentPartKind.Header, document.Paragraphs.Single(p => p.Index == logo.ParagraphIndex).Part!.Kind);
        Assert.NotEmpty(logo.Data);
    }

    private static DocumentPart PartOf(DocumentModel document, DocumentLocation location) =>
        document.Paragraphs.Single(p => p.Index == location.ParagraphIndex).Part!;

    private string Create(string name, string headerText, string? footerLine = "Approved by QA", string? revision = "Rev A",
        string? footnote = null, string? pageField = null, bool headerImage = false)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        var bodyRun = new Run(new Text("Body text that stays the same."));
        body.Append(new Paragraph(bodyRun));
        var section = new SectionProperties();

        var header = main.AddNewPart<HeaderPart>();
        header.Header = new Header(new Paragraph(new Run(new Text(headerText))));
        if (headerImage)
        {
            var imagePart = header.AddImagePart(ImagePartType.Png, "rIdLogo");
            using var stream = new MemoryStream(ContentDifferenceTests.CreateTextImage("ACME"));
            imagePart.FeedData(stream);
            header.Header.Append(new Paragraph(new Run(new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 900000, Cy = 300000 },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1, Name = "Logo" },
                new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(
                    new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                        new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                            new DocumentFormat.OpenXml.Drawing.Blip { Embed = "rIdLogo" })
                    ))))))));
        }
        section.Append(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) });

        if (footerLine is not null)
        {
            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = new Footer(new Paragraph(new Run(new Text(footerLine))));
            if (revision is not null)
                footer.Footer.Append(new Paragraph(new Run(new Text("Rev ")), new Run(new Text(revision.Substring(4)))));
            if (pageField is not null)
                footer.Footer.Append(new Paragraph(
                    new Run(new Text("Page ") { Space = SpaceProcessingModeValues.Preserve }),
                    new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                    new Run(new FieldCode(" PAGE ")),
                    new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                    new Run(new Text(pageField)),
                    new Run(new FieldChar { FieldCharType = FieldCharValues.End }),
                    new Run(new Text(" of ") { Space = SpaceProcessingModeValues.Preserve }),
                    new SimpleField(new Run(new Text("9"))) { Instruction = " NUMPAGES " }));
            section.Append(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) });
        }

        if (footnote is not null)
        {
            var notes = main.AddNewPart<FootnotesPart>();
            notes.Footnotes = new Footnotes(
                new Footnote(new Paragraph(new Run(new SeparatorMark()))) { Type = FootnoteEndnoteValues.Separator, Id = -1 },
                new Footnote(new Paragraph(new Run(new FootnoteReferenceMark()), new Run(new Text(" " + footnote)))) { Id = 1 });
            bodyRun.Append(new FootnoteReference { Id = 1 });
        }

        body.Append(section);
        main.Document.Save();
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
