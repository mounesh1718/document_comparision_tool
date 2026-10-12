using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.History;
using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>The comparison history: what is saved, duplicates, deleting, CSV export, and the History window.</summary>
public sealed class HistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.History.{Guid.NewGuid():N}");

    [Fact]
    [Trait("TC", "TC-HIS-01")]
    public void A_comparison_is_saved_with_its_files_result_and_differences()
    {
        var (reference, current) = CopySamples();
        var history = new ComparisonHistory(Path.Combine(_directory, "history.jsonl"));
        var result = Compare(reference, current);

        var saved = history.Record(result, reference, current, new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero));

        var entry = Assert.Single(history.Load());
        Assert.Equal(saved!.Id, entry.Id);
        Assert.Equal("Sample-Reference-RevA.docx", entry.Reference.Name);
        Assert.Equal(result.ReferenceHash, entry.Reference.Sha256);
        Assert.Equal(result.CurrentHash, entry.Current.Sha256);
        Assert.Equal(Environment.UserName, entry.Reviewer);
        Assert.Equal(21, entry.Differences);
        Assert.Equal("21 differences · 1 to check yourself", entry.Result);
        Assert.Equal(3, entry.ByChange["Row missing from Current"]);
        var assay = Assert.Single(entry.Findings, finding => finding.Reference == "95.0%");
        Assert.Equal("90.0%", assay.Current);
        Assert.EndsWith("Table 2, Row 4, Column 2 (Assay · Acceptance criteria)", assay.ReferenceLocation);
        Assert.Contains(entry.Findings, finding => finding.Change == "Space missing in Current" && finding.Reference == "[space]");
        Assert.Equal(HistoryFileState.Unchanged, ComparisonHistory.StateOf(entry.Current));
    }

    [Fact]
    [Trait("TC", "TC-HIS-02")]
    public void Repeated_comparisons_are_saved_once_and_entries_can_be_deleted()
    {
        var (reference, current) = CopySamples();
        var file = Path.Combine(_directory, "history.jsonl");
        var history = new ComparisonHistory(file);
        var first = history.Record(Compare(reference, current), reference, current, DateTimeOffset.Now.AddMinutes(-5))!;

        // The same unchanged files again: no duplicate.
        Assert.Equal(first.Id, history.Record(Compare(reference, current), reference, current)!.Id);
        Assert.Single(history.Load());

        // Current replaced by another version (here an identical copy of the approved file): a new entry, newest
        // first; the old entry now says the Current file has changed since.
        File.Copy(reference, current, overwrite: true);
        var second = history.Record(Compare(reference, current), reference, current)!;
        var entries = history.Load();
        Assert.Equal([second.Id, first.Id], entries.Select(entry => entry.Id));
        Assert.Equal("Exact File Match", second.Result);
        Assert.Equal(HistoryFileState.Changed, ComparisonHistory.StateOf(first.Current));

        // A damaged line (an interrupted write) is skipped; the other entries remain.
        File.AppendAllText(file, "{\"Id\":\"broken" + Environment.NewLine);
        Assert.Equal(2, history.Load().Count);

        history.Delete(first.Id);
        Assert.Equal(second.Id, Assert.Single(history.Load()).Id);
        history.Clear();
        Assert.Empty(history.Load());
        File.Delete(current);
        Assert.Equal(HistoryFileState.Missing, ComparisonHistory.StateOf(second.Current));
    }

    [Fact]
    [Trait("TC", "TC-HIS-03")]
    public void History_exports_to_csv_safely()
    {
        var entry = new HistoryEntry("id", new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero), "qa.reviewer", "PC-1",
            new HistoryFile("/docs/Spec, Rev A.docx", "Spec, Rev A.docx", "AB"), new HistoryFile("/docs/Spec \"B\".docx", "Spec \"B\".docx", "CD"),
            "Different", "2 differences", 2, 0, new Dictionary<string, int> { ["Text changed"] = 2 },
            [new HistoryFinding("TXT-001", "Text changed", "=SUM(A1)", "-0.5%", "Page 1, Paragraph 2", "Page 1, Paragraph 2", false),
             new HistoryFinding("TXT-002", "Text changed", "line one\nline two", "ok", "", "", true)]);

        var list = ComparisonHistory.ToCsv([entry]);
        var findings = ComparisonHistory.FindingsToCsv(entry);

        Assert.StartsWith("Compared at,Reviewer,Computer,Reference file", list);
        Assert.Contains("\"Spec, Rev A.docx\"", list);          // commas are quoted
        Assert.Contains("\"Spec \"\"B\"\".docx\"", list);       // quotes are doubled
        Assert.Contains("'=SUM(A1)", findings);                 // formulas are shown as text, not run
        Assert.Contains("'-0.5%", findings);
        Assert.Contains("\"line one\nline two\"", findings);     // line breaks stay inside one cell
        Assert.Contains(",Yes", findings);
    }

    /// <summary>The History window, inside the shared UI test window (Avalonia can only be set up once per process).</summary>
    internal static void VerifyWindow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.HistoryWindow.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var reference = Path.Combine(directory, "Sample-Reference-RevA.docx");
            var current = Path.Combine(directory, "Sample-Current-RevB.docx");
            File.Copy(Path.Combine(FindSamples(), "Sample-Reference-RevA.docx"), reference);
            File.Copy(Path.Combine(FindSamples(), "Sample-Current-RevB.docx"), current);
            var history = new ComparisonHistory(Path.Combine(directory, "history.jsonl"));
            history.Record(Compare(reference, current), reference, current);
            HistoryEntry? comparedAgain = null;

            var window = new HistoryWindow(history, entry => comparedAgain = entry);
            window.Show();
            try
            {
                Layout(window);
                Assert.Equal(1, window.ListedCount);
                window.Select(0);
                Layout(window);
                var details = window.DetailsText;
                Assert.Contains("21 differences · 1 to check yourself", details);
                Assert.Contains("Sample-Reference-RevA.docx", details);
                Assert.Contains("Unchanged since this comparison", details);
                Assert.Contains("Row missing from Current  3", details);

                window.CompareSelectedAgain();
                Assert.NotNull(comparedAgain);
                Assert.Equal(current, comparedAgain!.Current.Path);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private (string Reference, string Current) CopySamples()
    {
        Directory.CreateDirectory(_directory);
        var reference = Path.Combine(_directory, "Sample-Reference-RevA.docx");
        var current = Path.Combine(_directory, "Sample-Current-RevB.docx");
        File.Copy(Path.Combine(FindSamples(), "Sample-Reference-RevA.docx"), reference, overwrite: true);
        File.Copy(Path.Combine(FindSamples(), "Sample-Current-RevB.docx"), current, overwrite: true);
        return (reference, current);
    }

    private static ComparisonResult Compare(string reference, string current) =>
        new DocumentComparisonService(new NoImageText()).Compare(reference, current);

    private static string FindSamples()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "samples", "Sample-Reference-RevA.docx")))
                return Path.Combine(directory.FullName, "samples");
        throw new DirectoryNotFoundException("samples folder not found");
    }

    private static void Layout(Window window)
    {
        for (var pass = 0; pass < 5; pass++)
        {
            window.Measure(new Size(1180, 780));
            window.Arrange(new Rect(0, 0, 1180, 780));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
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
