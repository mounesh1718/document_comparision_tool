using DocumentChecker.Engine.Comparison;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace DocumentChecker.Engine.Reporting;

public sealed class ComparisonReportGenerator
{
    public void Generate(ComparisonResult result, string outputPath)
    {
        if (!result.IsSuccess)
            throw new InvalidOperationException("A report can only be created for a completed comparison.");

        if (GlobalFontSettings.FontResolver is null)
            GlobalFontSettings.FontResolver = new LocalFontResolver();

        var document = CreateDocument();
        var section = document.AddSection();
        ConfigurePage(section);
        AddPageChrome(section);
        AddReportHeader(section, result);

        if (result.Differences.Count == 0)
        {
            AddHeading(section, "No differences found in checked content", "ReportHeading1");
            section.AddParagraph("No differences were found within the comparison scope. Only Exact File Match certifies identical file bytes. Metadata and package identifiers are excluded from content comparison.");
        }
        else
        {
            AddHeading(section, "Difference summary", "ReportHeading1");
            AddCategorySummary(section, result.Differences);
            AddHeading(section, "Detailed review", "ReportHeading1");
            foreach (var difference in result.Differences)
                AddDifferenceCard(section, difference);
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(outputPath);
    }

    private static Document CreateDocument()
    {
        var document = new Document();
        document.Info.Title = "DOCX Comparison Report";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 9.5;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
        ConfigureHeading(document.Styles.AddStyle("ReportTitle", StyleNames.Normal), 19, Colors.DarkSlateGray, 8, 3);
        ConfigureHeading(document.Styles.AddStyle("ReportHeading1", StyleNames.Normal), 13, Colors.DarkSlateGray, 12, 5);
        ConfigureHeading(document.Styles.AddStyle("DifferenceTitle", StyleNames.Normal), 10.5, Colors.DarkSlateGray, 11, 3);
        var label = document.Styles.AddStyle("ReportLabel", StyleNames.Normal);
        label.Font.Bold = true;
        label.Font.Color = Colors.DarkSlateGray;
        label.Font.Size = 8.5;
        return document;
    }

    private static void ConfigurePage(Section section)
    {
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.65);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.55);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.6);
    }

    private static void AddPageChrome(Section section)
    {
        var header = section.Headers.Primary.AddParagraph();
        header.AddText("DOCUMENT CHECKER | COMPARISON REPORT");
        header.Format.Font.Name = "Arial";
        header.Format.Font.Size = 7.5;
        header.Format.Font.Bold = true;
        header.Format.Font.Color = Colors.Gray;
        header.Format.Borders.Bottom.Width = Unit.FromPoint(0.5);
        header.Format.Borders.Bottom.Color = Colors.LightGray;

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Right;
        footer.Format.Font.Name = "Arial";
        footer.Format.Font.Size = 7.5;
        footer.Format.Font.Color = Colors.Gray;
        footer.AddText("Local, read-only comparison | Page ");
        footer.AddPageField();
    }

    private static void AddReportHeader(Section section, ComparisonResult result)
    {
        AddHeading(section, "DOCX Comparison Report", "ReportTitle");
        var status = section.AddParagraph(StatusText(result.Status));
        status.Format.Font.Bold = true;
        status.Format.Font.Color = result.Status == ComparisonStatus.Different ? Colors.DarkRed : Colors.DarkGreen;
        status.Format.SpaceAfter = Unit.FromPoint(9);

        var details = section.AddTable();
        details.Borders.Width = Unit.FromPoint(0.5);
        details.Borders.Color = Colors.LightGray;
        details.AddColumn(Unit.FromCentimeter(3.1));
        details.AddColumn(Unit.FromCentimeter(13.5));
        AddKeyValueRow(details, "Reference document", result.ReferenceDocument?.FileName ?? "Not available");
        AddKeyValueRow(details, "Current document", result.CurrentDocument?.FileName ?? "Not available");
        AddKeyValueRow(details, "Review findings", $"{result.Differences.Count} finding(s); {result.Differences.Count(d => d.ReviewNote is not null)} need verification");
        AddKeyValueRow(details, "Comparison scope", "Body text, headers, footers, footnotes, endnotes, direct formatting, basic tables and embedded images. Page-number and date fields in headers and footers are compared as fields. Metadata is excluded. OCR, inherited styles, full page layout and unsupported parts are not certified; review flagged content against the originals.");
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(3);
    }

    private static void AddCategorySummary(Section section, IReadOnlyList<DocumentDifference> differences)
    {
        var table = section.AddTable();
        table.Borders.Width = Unit.FromPoint(0.5);
        table.Borders.Color = Colors.LightGray;
        table.AddColumn(Unit.FromCentimeter(12.6));
        table.AddColumn(Unit.FromCentimeter(4));
        var header = table.AddRow();
        header.Shading.Color = Colors.LightGray;
        header.Cells[0].AddParagraph("Change type").Style = "ReportLabel";
        header.Cells[1].AddParagraph("Count").Style = "ReportLabel";

        foreach (var category in differences.GroupBy(OperationText).OrderBy(group => group.Key))
        {
            var row = table.AddRow();
            row.Cells[0].AddParagraph(category.Key);
            var count = row.Cells[1].AddParagraph(category.Count().ToString());
            count.Format.Alignment = ParagraphAlignment.Center;
        }
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(3);
    }

    private static void AddDifferenceCard(Section section, DocumentDifference difference)
    {
        var title = section.AddParagraph($"{difference.Id} | {OperationText(difference)}", "DifferenceTitle");
        title.Format.KeepWithNext = true;

        var metadata = section.AddTable();
        metadata.Borders.Width = Unit.FromPoint(0.5);
        metadata.Borders.Color = Colors.LightGray;
        metadata.AddColumn(Unit.FromCentimeter(3.1));
        metadata.AddColumn(Unit.FromCentimeter(13.5));
        AddKeyValueRow(metadata, "Changed property", difference.Property);
        AddKeyValueRow(metadata, "Reference location", difference.ReferenceLocation?.Display ?? "No corresponding content");
        AddKeyValueRow(metadata, "Current location", difference.CurrentLocation?.Display ?? "No corresponding content");

        var values = section.AddTable();
        values.Borders.Width = Unit.FromPoint(0.5);
        values.Borders.Color = Colors.LightGray;
        values.AddColumn(Unit.FromCentimeter(8.3));
        values.AddColumn(Unit.FromCentimeter(8.3));
        var headers = values.AddRow();
        headers.Cells[0].Shading.Color = Colors.MistyRose;
        headers.Cells[1].Shading.Color = Colors.Honeydew;
        headers.Cells[0].AddParagraph("REFERENCE - SOURCE OF TRUTH").Style = "ReportLabel";
        headers.Cells[1].AddParagraph("CURRENT - DOCUMENT BEING CHECKED").Style = "ReportLabel";
        var valueRow = values.AddRow();
        valueRow.Cells[0].Shading.Color = Colors.MistyRose;
        valueRow.Cells[1].Shading.Color = Colors.Honeydew;
        AddCellText(valueRow.Cells[0], DisplayValue(difference.ReferenceValue));
        AddCellText(valueRow.Cells[1], DisplayValue(difference.CurrentValue));

        var context = section.AddParagraph();
        context.Format.SpaceBefore = Unit.FromPoint(3);
        context.Format.SpaceAfter = Unit.FromPoint(10);
        context.AddFormattedText("Context: ", TextFormat.Bold);
        context.AddText(DisplayValue(difference.NearbyText));
        if (!string.IsNullOrWhiteSpace(difference.ReviewNote))
        {
            context.AddLineBreak();
            context.AddText(difference.ReviewNote);
        }
    }

    private static void AddKeyValueRow(Table table, string label, string value)
    {
        var row = table.AddRow();
        row.Cells[0].Shading.Color = Colors.WhiteSmoke;
        row.Cells[0].AddParagraph(label).Style = "ReportLabel";
        AddCellText(row.Cells[1], DisplayValue(value));
    }

    private static void AddCellText(Cell cell, string value)
    {
        var paragraph = cell.AddParagraph(value);
        paragraph.Format.SpaceAfter = Unit.FromPoint(2);
        paragraph.Format.SpaceBefore = Unit.FromPoint(2);
    }

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value;

    private static string OperationText(DocumentDifference difference) => difference.Category switch
    {
        DifferenceCategory.Review => "Needs review",
        DifferenceCategory.ParagraphAdded => "Inserted paragraph",
        DifferenceCategory.ParagraphRemoved => "Deleted paragraph",
        DifferenceCategory.Table => "Table changed",
        DifferenceCategory.Formatting => "Formatting changed",
        DifferenceCategory.Image when difference.IsRecognizedText => "Recognized value - verify (OCR)",
        DifferenceCategory.Image => "Image changed",
        DifferenceCategory.Text when difference.ReferenceValue == "(no corresponding content)" => "Inserted text",
        DifferenceCategory.Text when difference.CurrentValue == "(no corresponding content)" => "Deleted text",
        _ => "Text changed"
    };

    private static string StatusText(ComparisonStatus status) => status switch
    {
        ComparisonStatus.ExactFileMatch => "Exact File Match",
        ComparisonStatus.DocumentDataMatch => "No differences found in checked content",
        ComparisonStatus.Different => "Differences or review items found",
        _ => "Comparison could not be completed"
    };

    private static void AddHeading(Section section, string text, string style) =>
        section.AddParagraph(text, style);

    private static void ConfigureHeading(Style style, double size, Color color, double spaceBefore, double spaceAfter)
    {
        style.Font.Name = "Arial";
        style.Font.Size = size;
        style.Font.Bold = true;
        style.Font.Color = color;
        style.ParagraphFormat.SpaceBefore = Unit.FromPoint(spaceBefore);
        style.ParagraphFormat.SpaceAfter = Unit.FromPoint(spaceAfter);
    }
}
