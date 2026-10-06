using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using DocumentChecker.Engine.Reporting;
using Xunit;

namespace DocumentChecker.Engine.Tests;

public sealed class ComparisonReportGeneratorTests
{
    [Fact]
    public void Generate_creates_a_pdf_only_when_explicitly_called()
    {
        var outputPath = Environment.GetEnvironmentVariable("DOCHECK_PDF_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), $"DocumentChecker.{Guid.NewGuid():N}.pdf");
        var result = new ComparisonResult(
            ComparisonStatus.Different,
            new DocumentModel("Reference.docx", []),
            new DocumentModel("Current.docx", []),
            [new DocumentDifference(
                "TXT-001",
                DifferenceCategory.Text,
                "Text",
                "30",
                "45",
                new DocumentLocation(4, 1),
                new DocumentLocation(4, 1),
                new DocumentRange(4, 10, 2),
                new DocumentRange(4, 10, 2),
                "Reference: Payment within 30 days | Current: Payment within 45 days"),
            new DocumentDifference(
                "FMT-002",
                DifferenceCategory.Formatting,
                "Subscript",
                "On",
                "Off",
                new DocumentLocation(7, 0),
                new DocumentLocation(7, 0),
                new DocumentRange(7, 2, 1),
                new DocumentRange(7, 2, 1),
                "Reference: Chemical notation CO2 uses a subscript | Current: Chemical notation CO2 does not use a subscript"),
            new DocumentDifference(
                "DEL-003",
                DifferenceCategory.ParagraphRemoved,
                "Paragraph",
                "The approval wording must be retained for the final release.",
                "(no corresponding content)",
                new DocumentLocation(12, 0),
                null,
                new DocumentRange(12, 0, 60),
                null,
                "Reference paragraph was not found in the Current document.")],
            [],
            "reference-hash",
            "current-hash");

        new ComparisonReportGenerator().Generate(result, outputPath);

        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);
    }
}
