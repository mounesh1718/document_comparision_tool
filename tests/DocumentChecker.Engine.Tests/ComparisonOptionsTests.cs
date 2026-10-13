using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.History;
using DocumentChecker.Engine.Reading;
using DocumentChecker.Engine.Reporting;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>Switching headers and footers off: not compared, not shown, and said so in the report, history and settings.</summary>
public sealed class ComparisonOptionsTests : IDisposable
{
    private static readonly ComparisonOptions WithoutHeadersAndFooters = new(CompareHeadersAndFooters: false);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Options.{Guid.NewGuid():N}");

    [Fact]
    [Trait("TC", "TC-OPT-01")]
    public void Without_headers_and_footers_their_changes_are_not_reported_and_they_are_not_shown()
    {
        var (reference, current) = Samples();
        var service = new DocumentComparisonService(new NoImageText());

        var all = service.Compare(reference, current);
        var withoutParts = service.Compare(reference, current, WithoutHeadersAndFooters);

        Assert.False(withoutParts.UsedOptions.CompareHeadersAndFooters);
        Assert.True(all.UsedOptions.CompareHeadersAndFooters);
        // The header ("Confidential") and footer ("Rev A") changes are gone; everything else, footnotes included, remains.
        Assert.Equal(all.Differences.Count - 2, withoutParts.Differences.Count);
        Assert.DoesNotContain(withoutParts.Differences, difference => difference.ReferenceValue is "Confidential" or "A");
        Assert.Contains(withoutParts.Differences, difference => difference.ReferenceValue == "SP-2024-01.");
        foreach (var document in new[] { withoutParts.ReferenceDocument!, withoutParts.CurrentDocument! })
        {
            Assert.DoesNotContain(document.Paragraphs, paragraph => paragraph.Part?.Kind is DocumentPartKind.Header or DocumentPartKind.Footer);
            Assert.DoesNotContain(document.DisplayBlocks.OfType<PartDisplayBlock>(), part => part.Part.Kind is DocumentPartKind.Header or DocumentPartKind.Footer);
            Assert.Contains(document.DisplayBlocks.OfType<PartDisplayBlock>(), part => part.Part.Kind == DocumentPartKind.Footnote);
        }
    }

    [Fact]
    [Trait("TC", "TC-OPT-02")]
    public void Documents_that_differ_only_in_the_header_match_when_headers_are_switched_off()
    {
        var reference = Create("reference.docx", "ACME Ltd — Confidential");
        var current = Create("current.docx", "ACME Ltd — Internal");
        var service = new DocumentComparisonService(new NoImageText());

        Assert.Equal(ComparisonStatus.Different, service.Compare(reference, current).Status);
        var result = service.Compare(reference, current, WithoutHeadersAndFooters);
        Assert.Equal(ComparisonStatus.DocumentDataMatch, result.Status);
        Assert.Empty(result.Differences);
    }

    [Fact]
    [Trait("TC", "TC-OPT-03")]
    public void Report_history_and_settings_record_that_headers_and_footers_were_not_compared()
    {
        var (reference, current) = Samples();
        var result = new DocumentComparisonService(new NoImageText()).Compare(reference, current, WithoutHeadersAndFooters);

        // The PDF report says so in its scope.
        var pdf = Path.Combine(_directory, "report.pdf");
        new ComparisonReportGenerator().Generate(result, pdf);
        string text;
        using (var document = PdfDocument.Open(pdf))
            text = string.Join("\n", document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
        Assert.Contains("Headers and footers were NOT compared", text);

        // The history keeps the choice, and the same files compared with headers on are a separate entry.
        var history = new ComparisonHistory(Path.Combine(_directory, "history.jsonl"));
        var withoutParts = history.Record(result, reference, current)!;
        var withParts = history.Record(new DocumentComparisonService(new NoImageText()).Compare(reference, current), reference, current)!;
        Assert.False(withoutParts.HeadersAndFootersCompared);
        Assert.True(withParts.HeadersAndFootersCompared);
        Assert.Equal(2, history.Load().Count);
        Assert.Contains(",No,", ComparisonHistory.ToCsv([withoutParts]));

        // The choice is remembered between sessions.
        var settings = Path.Combine(_directory, "settings.json");
        Assert.True(AppSettings.Load(settings).CompareHeadersAndFooters);   // default when nothing is saved
        new AppSettings(CompareHeadersAndFooters: false).Save(settings);
        Assert.False(AppSettings.Load(settings).CompareHeadersAndFooters);
    }

    /// <summary>
    /// The checkbox under Compare, inside the shared UI test (Avalonia can only be set up once per process):
    /// unticking it compares again without headers and footers and hides them; ticking it brings them back.
    /// </summary>
    internal static void VerifyCheckBox()
    {
        var samples = FindSamples();
        var window = new MainWindow();
        window.Show();
        try
        {
            Set(window, "_referencePath", Path.Combine(samples, "Sample-Reference-RevA.docx"));
            Set(window, "_currentPath", Path.Combine(samples, "Sample-Current-RevB.docx"));
            var checkBox = window.FindControl<Avalonia.Controls.CheckBox>("HeadersFootersCheckBox")!;
            checkBox.IsChecked = true;
            Wait((Task)Invoke(window, "RunComparisonAsync", false));
            Assert.Contains("HEADER", Labels(window));
            Assert.Contains("FOOTER", Labels(window));

            checkBox.IsChecked = false;   // compares again at once
            WaitUntil(() => !(bool)Get(window, "_comparing") && Result(window).UsedOptions.CompareHeadersAndFooters == false);
            Layout(window);
            Assert.DoesNotContain("HEADER", Labels(window));
            Assert.DoesNotContain("FOOTER", Labels(window));
            Assert.Contains("FOOTNOTE 1", Labels(window));
            Assert.EndsWith("headers & footers not checked", window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Equal(19, Result(window).Differences.Count);
            Assert.False(AppSettings.Load().CompareHeadersAndFooters);

            checkBox.IsChecked = true;
            WaitUntil(() => !(bool)Get(window, "_comparing") && Result(window).UsedOptions.CompareHeadersAndFooters);
            Layout(window);
            Assert.Contains("HEADER", Labels(window));
            Assert.Equal(21, Result(window).Differences.Count);
        }
        finally
        {
            window.Close();
        }
    }

    private static ComparisonResult Result(MainWindow window) => (ComparisonResult)Get(window, "_comparisonResult");

    /// <summary>The part labels drawn in the Reference pane ("HEADER", "FOOTNOTE 1", …).</summary>
    private static List<string?> Labels(MainWindow window) => window.FindControl<StackPanel>("ReferenceDocumentPanel")!
        .GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

    private static void Wait(Task task) => WaitUntil(() => task.IsCompleted);

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the comparison.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    private static void Layout(Window window)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            window.Measure(new Size(1464, 840));
            window.Arrange(new Rect(0, 0, 1464, 840));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private (string Reference, string Current) Samples()
    {
        Directory.CreateDirectory(_directory);
        return (Path.Combine(FindSamples(), "Sample-Reference-RevA.docx"), Path.Combine(FindSamples(), "Sample-Current-RevB.docx"));
    }

    private string Create(string name, string headerText)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body(new Paragraph(new Run(new Text("Body text that stays the same."))));
        main.Document = new Document(body);
        var header = main.AddNewPart<HeaderPart>();
        header.Header = new Header(new Paragraph(new Run(new Text(headerText))));
        body.Append(new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) }));
        main.Document.Save();
        return path;
    }

    private static string FindSamples()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "samples", "Sample-Reference-RevA.docx")))
                return Path.Combine(directory.FullName, "samples");
        throw new DirectoryNotFoundException("samples folder not found");
    }

    private static object Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object Invoke(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!;

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
