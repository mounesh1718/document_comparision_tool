using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reporting;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>
/// The sample documents in samples/ (see samples/README.md) compared end to end. Any change in what the tool
/// reports for them fails here, so update samples/README.md and the expected list together.
/// </summary>
public sealed class SampleDocumentTests : IDisposable
{
    private const string Absent = "(no corresponding content)";
    private readonly string _reportPath = Path.Combine(Path.GetTempPath(), $"DocumentChecker.SampleReport.{Guid.NewGuid():N}.pdf");

    /// <summary>Every planned change in samples/README.md, in the order the tool reports it.</summary>
    private static readonly (string Operation, string Reference, string Current)[] Expected =
    [
        ("Text changed", "Confidential", "Internal Use"),
        ("Text alignment changed", "center", "left"),
        ("Inserted paragraph", Absent, "This specification applies to commercial batches only."),
        ("Space missing in Current", " ", Absent),
        ("Subscript removed", "On", "Off"),
        ("Bold changed", "On", "Off"),
        ("Text hidden in Current", "Off", "On"),
        ("Text changed", "≤", "≥"),
        ("Text changed", "30", "45"),
        ("Deleted paragraph", "Retain samples must be stored for one year after expiry.", Absent),
        ("Text changed", "A", "B"),
        ("Text changed", "SP-2024-01.", "SP-2025-01."),
        ("Text changed", "120.0", "125.0"),
        ("Text changed", "373.0", "378.0"),
        ("Text changed", "95.0%", "90.0%"),
        ("Text changed", "105.0%", "110.0%"),
        ("Text changed", "30", "45"),
        ("Row missing from Current", "Water content", Absent),
        ("Row missing from Current", "NMT 2.0% w/w", Absent),
        ("Row missing from Current", "AM-104", Absent),
        ("Visual image change", "Reference image area", "Current image area"),
    ];

    [Fact]
    [Trait("TC", "TC-SMP-01")]
    public void Sample_documents_report_exactly_the_planned_changes()
    {
        var result = CompareSamples();

        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.Equal(Expected, result.Differences.Select(d => (FindingText.Operation(d), d.ReferenceValue, d.CurrentValue)).ToArray());
        // The content control is read and compared, so it is not a review item; page-number fields are not reported.
        Assert.DoesNotContain(result.Differences, d => d.Category == DifferenceCategory.Review);
        Assert.DoesNotContain(result.Differences, d => d.ReferenceValue.Contains("Page") || d.CurrentValue.Contains("Page"));
    }

    [Fact]
    [Trait("TC", "TC-SMP-01")]
    public void Sample_findings_are_located_as_a_reviewer_finds_them_in_Word()
    {
        var result = CompareSamples();
        string Where(string referenceValue) =>
            FindingText.Location(result.ReferenceDocument, result.Differences.First(d => d.ReferenceValue == referenceValue).ReferenceLocation);

        Assert.Equal("Header, Paragraph 1", Where("Confidential"));
        Assert.Equal("Footer, Paragraph 1", Where("A"));
        Assert.Equal("Footnote 1, Paragraph 1", Where("SP-2024-01."));
        // Table cells are not counted as paragraphs: "Results must be reported…" is the 12th body paragraph.
        Assert.EndsWith(", Paragraph 12", Where("30"));
        Assert.EndsWith("Table 2, Row 4, Column 2 (Assay · Acceptance criteria)", Where("95.0%"));
    }

    [Fact]
    [Trait("TC", "TC-RPT-02")]
    public void Sample_report_names_parts_shows_whitespace_and_uses_the_app_wording()
    {
        var result = CompareSamples();
        new ComparisonReportGenerator().Generate(result, _reportPath);

        string text;
        using (var pdf = PdfDocument.Open(_reportPath))
            text = string.Join("\n", pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));

        Assert.Contains("Header, Paragraph 1", text);
        Assert.Contains("Footnote 1, Paragraph 1", text);
        Assert.Contains("[space]", text);
        Assert.Contains("Row missing from Current", text);
        Assert.Contains("Text hidden in Current", text);
        // No internal storage details, no paragraph numbers that do not exist in the body, no blank values shown as "(empty)".
        Assert.DoesNotContain(", Run ", text);
        Assert.DoesNotContain("Paragraph 54", text);
        Assert.DoesNotContain("(empty)", text);
        Assert.DoesNotContain("Deleted paragraph 4", text);
    }

    private static ComparisonResult CompareSamples()
    {
        var samples = FindSamples();
        // OCR is replaced so the result is the same on every operating system.
        return new DocumentComparisonService(new NoImageText()).Compare(
            Path.Combine(samples, "Sample-Reference-RevA.docx"), Path.Combine(samples, "Sample-Current-RevB.docx"));
    }

    /// <summary>The repository's samples folder, found by walking up from the test output folder.</summary>
    private static string FindSamples()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var samples = Path.Combine(directory.FullName, "samples");
            if (File.Exists(Path.Combine(samples, "Sample-Reference-RevA.docx")))
                return samples;
        }
        throw new DirectoryNotFoundException("samples/Sample-Reference-RevA.docx was not found above " + AppContext.BaseDirectory);
    }

    private sealed class NoImageText : IImageTextReader
    {
        public ImageTextResult Read(byte[] imageData) => new([], "OCR not used in this test.");
    }

    public void Dispose()
    {
        if (File.Exists(_reportPath))
            File.Delete(_reportPath);
    }
}
