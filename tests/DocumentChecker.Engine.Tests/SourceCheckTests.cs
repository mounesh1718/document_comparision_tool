using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using DocumentChecker.Engine.Sources;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>Source check: the answers of a question-based document checked against the PDFs of a source folder.</summary>
public sealed class SourceCheckTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.SourceCheck.{Guid.NewGuid():N}");

    [Fact]
    [Trait("TC", "TC-SRC-09")]
    public void Questions_answers_and_facts_are_found_in_a_question_based_document()
    {
        var document = new DocumentReader().Read(SourceCheckSamples.CreateDocument(Path.Combine(_directory, "QOS.docx"))).Document!;

        var questions = QuestionAnswerReader.Read(document);

        Assert.Equal(3, questions.Count);
        Assert.Equal(("2.3.S.1", "What are the nomenclature, molecular structure, molecular formula, CAS number, molecular weight and pharmacological class of the drug?"),
            (questions[0].Section, questions[0].Text));
        Assert.Equal("2.3.S.4", questions[1].Section);
        Assert.Equal("2.3.S.7", questions[2].Section);
        // One fact per table row, label in the first cell; the header row of the specification table is not a fact.
        Assert.Equal(["Recommended International Nonproprietary Name (rINN)", "United States Adopted Name (USAN)", "Compendial Name",
                "Molecular Formula", "Molecular Weight", "Chemical Abstracts Service (CAS) Registry Number", "Pharmacological Class"],
            questions[0].Facts.Select(fact => fact.Label));
        Assert.Equal("C5H11NO2S", questions[0].Facts[3].Value);   // subscripts are part of the formula
        Assert.DoesNotContain(questions[1].Facts, fact => fact.Label == "Test");
        Assert.Equal("NMT 0.2% | AM-102", questions[1].Facts.Single(fact => fact.Label == "Total impurities").Value);
        // Sentences: a list of batch numbers stays one fact; a sentence part with a value takes the words before it as its label.
        var batches = questions[1].Facts.Single(fact => fact.Kind == SourceFactKind.Value);
        Assert.Contains("PN-2203", batches.Value);
        var retest = questions[2].Facts.Single(fact => fact.Value.Contains("36 months"));
        Assert.Equal("retest period", retest.Label);
        Assert.Contains(questions[2].Facts, fact => fact.Kind == SourceFactKind.Statement);
    }

    [Fact]
    [Trait("TC", "TC-SRC-10")]
    public void Every_fact_is_checked_against_the_source_pdfs_with_its_evidence()
    {
        var (document, library) = Samples();

        var result = SourceChecker.Check(document, library);

        Assert.Null(result.Problem);
        SourceFactResult Of(string label) => result.Results.Single(item => item.Fact.Label == label);
        // Same value next to the same label.
        Assert.Equal(SourceVerdict.Matches, Of("Molecular Formula").Verdict);
        Assert.Equal(SourceVerdict.Matches, Of("Compendial Name").Verdict);
        Assert.Equal(SourceVerdict.Matches, Of("Assay").Verdict);
        Assert.Equal(SourceVerdict.Matches, Of("Loss on drying").Verdict);   // NMT 0.5% and USP <731>
        // The long label is found by its last words ("CAS Registry Number").
        var cas = Of("Chemical Abstracts Service (CAS) Registry Number");
        Assert.Equal(SourceVerdict.Matches, cas.Verdict);
        Assert.Equal("m3/32s-drug-sub/32s1-gen-info/nomenclature.pdf", cas.Evidence!.File.RelativePath);
        Assert.Equal(1, cas.Evidence.Page);
        Assert.Contains("52-67-5", cas.Evidence.Snippet);
        // A different value next to the label, found in the answer's own section (3.2.S.4 for 2.3.S.4) first.
        var impurities = Of("Total impurities");
        Assert.Equal(SourceVerdict.Different, impurities.Verdict);
        Assert.Contains("NMT 0.15%", impurities.SourceValue);
        Assert.Equal("m3/32s-drug-sub/32s4-contr-drug-sub/specification.pdf", impurities.Evidence!.File.RelativePath);
        var retest = result.Results.Single(item => item.Fact.Label == "retest period");
        Assert.Equal(SourceVerdict.Different, retest.Verdict);
        Assert.Contains("24 months", retest.SourceValue);
        // The value is printed, but under another label ("Relative molecular mass"): check yourself.
        var weight = Of("Molecular Weight");
        Assert.Equal(SourceVerdict.CheckYourself, weight.Verdict);
        Assert.Contains("149.21", weight.Evidence!.Snippet);
        // Batch numbers are identifiers: printed in the source is enough.
        Assert.Equal(SourceVerdict.Matches, result.Results.Single(item => item.Fact.Value.Contains("PN-2201")).Verdict);
        // Not in any source.
        Assert.Equal(SourceVerdict.NotFound, Of("Particle size").Verdict);
        // A sentence without values gets the passage that shares most of its words.
        var statement = result.Results.Single(item => item.Fact.Kind == SourceFactKind.Statement);
        Assert.Equal(SourceVerdict.CheckYourself, statement.Verdict);
        Assert.Contains("light-resistant", statement.Evidence!.Snippet);
        // The evidence points at the characters of the page text.
        var page = library.TextOf(impurities.Evidence.File)!.Pages[impurities.Evidence.Page - 1].Text;
        Assert.StartsWith("Total impurities", page.Substring(impurities.Evidence.Start, impurities.Evidence.Length));
        // …and is boxed on the drawn page, on the "Total impurities" line (the fifth line, 22 points apart from y = 80).
        var (drawn, boxes) = SourcePages.Draw(library.PathOf(impurities.Evidence.File), impurities.Evidence);
        var box = Assert.Single(boxes);
        Assert.InRange(box.Y + box.Height, 80 + 4 * 22 - 4, 80 + 4 * 22 + 6);
        Assert.InRange(box.X, 45, 55);
        Assert.True(box.X + box.Width > 450);   // to the end of "AM-102" in the third column
        Assert.Equal(595, drawn.Width, 0);   // A4
        // The CSV for Excel has one row per fact.
        var csv = result.ToCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(result.Results.Count + 1, csv.Length);
        Assert.Contains(csv, line => line.Contains("Total impurities") && line.Contains("Different in source") && line.Contains("specification.pdf"));
    }

    /// <summary>
    /// The Source check window, inside the shared UI test: the sample document checked against the sample source folder,
    /// the results listed by question, filtered, and a fact shown with its source page and the evidence boxed.
    /// </summary>
    internal static void VerifyWindow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.SourceCheckUi.{Guid.NewGuid():N}");
        try
        {
            var document = SourceCheckSamples.CreateDocument(Path.Combine(directory, "QOS-Sample.docx"));
            var sources = SourceCheckSamples.CreateSources(Path.Combine(directory, "m3"));
            var root = Path.Combine(directory, "Sources");
            SourceLibrary.Create("Penicillamine – Module 3", root).UploadFolder(sources, new NoImageText());
            var window = new SourceCheckWindow(document, root);
            window.Show();
            try
            {
                window.SelectLibrary("Penicillamine – Module 3");
                var check = window.ProceedAsync();
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!check.IsCompleted)
                {
                    Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the source check.");
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(20);
                }
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Assert.StartsWith("3 questions, 16 facts checked against \"Penicillamine – Module 3\": 10 match, 2 different, 1 not found, 3 to check yourself.",
                    window.StatusText);
                Assert.Equal((3, 16), (window.ListedQuestions, window.ListedFacts));
                Assert.Equal("Penicillamine – Module 3", DocumentChecker.Engine.History.AppSettings.Load().SourceFolder);
                // "Next to review" has selected the first fact that is not a match: the molecular weight, printed under another label.
                Assert.Contains("Check yourself", window.DetailsText);
                Assert.Contains("Molecular Weight", window.DetailsText);
                Assert.True(window.PageShown);

                window.SetFilter(SourceVerdict.Different);
                Assert.Equal((2, 2), (window.ListedQuestions, window.ListedFacts));
                window.SelectFact(result => result.Fact.Label == "Total impurities");
                Assert.Contains("NMT 0.15% AM-102", window.DetailsText);
                Assert.Contains("m3/32s-drug-sub/32s4-contr-drug-sub/specification.pdf", window.DetailsText);
                Assert.Contains("Page 1", window.DetailsText);
                Assert.True(window.PageShown);
                Assert.Equal(1, window.EvidenceBoxes);

                window.SetFilter(SourceVerdict.NotFound);
                window.SelectFact(_ => true);
                Assert.False(window.PageShown);
                Assert.Contains("Particle size", window.DetailsText);

                if (Environment.GetEnvironmentVariable("DOCHECK_SOURCECHECK_CAPTURE") is { Length: > 0 } capture)
                {
                    window.SetFilter(null);
                    window.SelectFact(result => result.Fact.Label == "Total impurities");
                    for (var pass = 0; pass < 4; pass++)
                    {
                        window.Measure(new Avalonia.Size(1320, 860));
                        window.Arrange(new Avalonia.Rect(0, 0, 1320, 860));
                        window.UpdateLayout();
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    }
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1320, 860), new Avalonia.Vector(96, 96));
                    bitmap.Render((Avalonia.Controls.Control)window.Content!);
                    bitmap.Save(capture);
                }
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("TC", "TC-SRC-11")]
    public void Values_are_compared_by_meaning_not_by_how_they_are_written()
    {
        var tokens = SourceTokens.ValueTokens("Assay 98.0 – 102.0 %, NMT 0.20%, not less than 1,000 units, CAS 52-67-5, AM-101, C5H11NO2S, see 3.2.S.4.1, USP <731>");

        Assert.Equal(["98", "102", "0.2", "1000", "52-67-5", "am-101", "c5h11no2s", "731"], tokens.Select(token => token.Key));
        Assert.Equal([null, null, "≤", "≥", null, null, null, null], tokens.Select(token => token.Operator));
        Assert.Equal(["32s41"], SourceTokens.SectionHints("as given in 3.2.S.4.1"));
        Assert.Equal("assay - x", SourceTokens.Normalize("Assay – x"));

        // A limit with the other sign is a different value.
        var (document, library) = Samples(specificationLine: "Total impurities\tNLT 0.2%\tAM-102");
        var impurities = SourceChecker.Check(document, library).Results.Single(item => item.Fact.Label == "Total impurities");
        Assert.Equal(SourceVerdict.Different, impurities.Verdict);
    }

    [Fact]
    [Trait("TC", "TC-SRC-12")]
    public void A_document_without_questions_or_a_folder_without_pdfs_is_explained()
    {
        Directory.CreateDirectory(_directory);
        var plain = Path.Combine(_directory, "plain.docx");
        using (var word = WordprocessingDocument.Create(plain, WordprocessingDocumentType.Document))
        {
            word.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("No questions here.")))));
        }
        var (questionDocument, library) = Samples();
        var empty = SourceLibrary.Create("Empty", Path.Combine(_directory, "Sources"));

        Assert.Contains("No questions were found", SourceChecker.Check(plain, library).Problem);
        Assert.Contains("no readable PDF pages", SourceChecker.Check(questionDocument, empty).Problem);
        Assert.NotNull(SourceChecker.Check(Path.Combine(_directory, "missing.docx"), library).Problem);
    }

    /// <summary>The sample document and a source folder with the sample PDFs uploaded.</summary>
    private (string Document, SourceLibrary Library) Samples(string? specificationLine = null)
    {
        var root = Path.Combine(_directory, Guid.NewGuid().ToString("N")[..8]);
        var document = SourceCheckSamples.CreateDocument(Path.Combine(root, "QOS-Sample.docx"));
        var sources = SourceCheckSamples.CreateSources(Path.Combine(root, "m3"), specificationLine);
        var library = SourceLibrary.Create("Samples", Path.Combine(root, "Sources"));
        library.UploadFolder(sources, new NoImageText());
        return (document, library);
    }

    private sealed class NoImageText : IImageTextReader
    {
        public ImageTextResult Read(byte[] imageData) => new([], "Text recognition is not used in this test.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>
/// A synthetic question-based Quality Overall Summary (the layout of a QbR QOS: section headings, numbered questions in a
/// shaded box, answers as label/value tables, a specification table and sentences) and source PDFs in an eCTD-like
/// folder. Some answers differ from the sources on purpose. Public data for penicillamine; no real submission content.
/// Set DOCHECK_SAMPLE_OUT to also write them to that folder (used to make samples/source-check).
/// </summary>
internal static class SourceCheckSamples
{
    public static string CreateDocument(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var body = new Body();
        word.AddMainDocumentPart().Document = new Document(body);

        body.Append(Heading("2.3.S DRUG SUBSTANCE (PENICILLAMINE, USP)"));
        body.Append(Heading("2.3.S.1 General Information"));
        body.Append(QuestionBox("1. What are the nomenclature, molecular structure, molecular formula, CAS number, molecular weight and pharmacological class of the drug?"));
        body.Append(Table(
            ["Recommended International Nonproprietary Name (rINN)", "Penicillamine"],
            ["United States Adopted Name (USAN)", "Penicillamine"],
            ["Compendial Name", "USP: Penicillamine"],
            ["Molecular Formula", "C|5|H|11|NO|2|S"],
            ["Molecular Weight", "149.21 g/mol"],
            ["Chemical Abstracts Service (CAS) Registry Number", "[52-67-5]"],
            ["Pharmacological Class", "Chelating agent"]));

        body.Append(Heading("2.3.S.4 Control of Drug Substance"));
        body.Append(QuestionBox("2. What is the drug substance specification? Does it include all the critical attributes?"));
        body.Append(Table(
            ["Test", "Acceptance criteria", "Method"],
            ["Assay", "97.0% – 100.5% (on dried basis)", "AM-101"],
            ["Loss on drying", "NMT 0.5%", "USP <731>"],
            ["Total impurities", "NMT 0.2%", "AM-102"],
            ["Residue on ignition", "NMT 0.1%", "USP <281>"],
            ["Particle size", "D90 NMT 150 µm", "AM-110"]));
        body.Append(Plain("The specification is provided in 3.2.S.4.1. Three batches (PN-2201, PN-2202, PN-2203) were tested and all results comply."));

        body.Append(Heading("2.3.S.7 Stability"));
        body.Append(QuestionBox("3. What is the proposed retest period and storage condition?"));
        body.Append(Plain("Based on 24 months long-term data at 25 °C/60% RH, a retest period of 36 months is proposed. " +
                          "The drug substance is stored in tight, light-resistant containers at controlled room temperature."));
        word.MainDocumentPart!.Document.Save();
        word.Dispose();   // closed before it is copied
        Copy(path);
        return path;
    }

    public static string CreateSources(string folder, string? specificationLine = null)
    {
        SourceLibraryTests.Pdf(Path.Combine(folder, "32s-drug-sub", "32s1-gen-info", "nomenclature.pdf"), [[
            "3.2.S.1.1 Nomenclature",
            "Recommended International Nonproprietary Name (rINN)\tPenicillamine",
            "United States Adopted Name (USAN)\tPenicillamine",
            "Compendial name\tPenicillamine USP",
            "CAS Registry Number\t52-67-5",
            "3.2.S.1.2 Structure",
            "Molecular formula\tC5H11NO2S",
            "Relative molecular mass\t149.21",
            "Pharmacological class\tChelating agent; antirheumatic"
        ]], password: null);
        SourceLibraryTests.Pdf(Path.Combine(folder, "32s-drug-sub", "32s4-contr-drug-sub", "specification.pdf"), [[
            "3.2.S.4.1 Specification",
            "Test\tAcceptance criteria\tMethod",
            "Assay\t97.0% - 100.5% (on dried basis)\tAM-101",
            "Loss on drying\tNMT 0.5%\tUSP <731>",
            specificationLine ?? "Total impurities\tNMT 0.15%\tAM-102",
            "Residue on ignition\tNMT 0.1%\tUSP <281>"
        ]], password: null);
        SourceLibraryTests.Pdf(Path.Combine(folder, "32s-drug-sub", "32s4-contr-drug-sub", "batch-analyses.pdf"), [[
            "3.2.S.4.4 Batch Analyses",
            "Batch\tAssay\tTotal impurities",
            "PN-2201\t99.4%\t0.08%",
            "PN-2202\t99.1%\t0.11%",
            "PN-2203\t99.6%\t0.07%",
            "All batches comply with the specification."
        ]], password: null);
        SourceLibraryTests.Pdf(Path.Combine(folder, "32s-drug-sub", "32s7-stab", "stability-summary.pdf"), [[
            "3.2.S.7.1 Stability Summary and Conclusions",
            "Long-term condition\t25 °C/60% RH\t24 months",
            "Accelerated condition\t40 °C/75% RH\t6 months",
            "Proposed retest period: 24 months",
            "Store in tight, light-resistant containers at controlled room temperature."
        ]], password: null);
        Copy(folder);
        return folder;
    }

    /// <summary>Also writes the file or folder to DOCHECK_SAMPLE_OUT, keeping its name.</summary>
    private static void Copy(string path)
    {
        if (Environment.GetEnvironmentVariable("DOCHECK_SAMPLE_OUT") is not { Length: > 0 } output)
            return;
        var target = Path.Combine(output, Path.GetFileName(path));
        if (File.Exists(path))
        {
            Directory.CreateDirectory(output);
            File.Copy(path, target, overwrite: true);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(path, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static Paragraph Heading(string text) =>
        new(new Run(new RunProperties(new Bold(), new FontSize { Val = "24" }), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Plain(string text) => new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    /// <summary>The question in a one-cell shaded box, as in a QbR QOS.</summary>
    private static Table QuestionBox(string text) => new(
        new TableProperties(Borders()),
        new TableGrid(new GridColumn { Width = "9000" }),
        new TableRow(new TableCell(
            new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "F9C66B" }),
            new Paragraph(new Run(new RunProperties(new Bold(), new Italic()), new Text(text) { Space = SpaceProcessingModeValues.Preserve })))));

    /// <summary>A table; the first column is bold. "|" in a cell separates normal and subscript parts ("C|5|H" → C₅H).</summary>
    private static Table Table(params string[][] rows)
    {
        var columns = rows.Max(row => row.Length);
        var table = new Table(new TableProperties(Borders()),
            new TableGrid(Enumerable.Range(0, columns).Select(_ => new GridColumn { Width = (9000 / columns).ToString() })));
        var header = columns >= 3;
        foreach (var (row, rowIndex) in rows.Select((row, index) => (row, index)))
        {
            var tableRow = new TableRow();
            foreach (var (cell, column) in row.Select((cell, index) => (cell, index)))
            {
                var bold = column == 0 || (header && rowIndex == 0);
                var paragraph = new Paragraph();
                var parts = cell.Split('|');
                for (var part = 0; part < parts.Length; part++)
                {
                    var properties = new RunProperties();
                    if (bold)
                        properties.Append(new Bold());
                    if (part % 2 == 1)
                        properties.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript });
                    paragraph.Append(new Run(properties, new Text(parts[part]) { Space = SpaceProcessingModeValues.Preserve }));
                }
                tableRow.Append(new TableCell(paragraph));
            }
            table.Append(tableRow);
        }
        return table;
    }

    private static TableBorders Borders() => new(
        new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
        new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
        new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 });
}
