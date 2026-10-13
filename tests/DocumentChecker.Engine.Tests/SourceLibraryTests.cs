using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.ExactView;
using DocumentChecker.Engine.Reporting;
using DocumentChecker.Engine.Sources;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DocumentChecker.Engine.Tests;

/// <summary>Source folders for Source check: created, uploaded with the same structure, verified, and their PDF text read.</summary>
public sealed class SourceLibraryTests : IDisposable
{
    private static readonly object FontGate = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.Sources.{Guid.NewGuid():N}");
    private string Root => Path.Combine(_directory, "Sources");
    private string Originals => Path.Combine(_directory, "Originals");

    [Fact]
    [Trait("TC", "TC-SRC-01")]
    public void Source_folders_are_created_listed_and_deleted_with_checked_names()
    {
        var library = SourceLibrary.Create("  Examplin 250 mg – Module 3 ", Root);

        Assert.Equal("Examplin 250 mg – Module 3", library.Name);
        Assert.Empty(library.Files);
        Assert.Equal("Examplin 250 mg – Module 3", Assert.Single(SourceLibrary.List(Root)).Name);
        Assert.Contains("already exists", Assert.Throws<ArgumentException>(() => SourceLibrary.Create("examplin 250 MG – module 3", Root)).Message);
        Assert.Contains("name", Assert.Throws<ArgumentException>(() => SourceLibrary.Create("   ", Root)).Message);
        Assert.Throws<ArgumentException>(() => SourceLibrary.Create("///", Root));
        // Characters a folder name cannot have are replaced on disk; the name itself is kept as typed.
        var slashed = SourceLibrary.Create("Product B: QOS/2026", Root);
        Assert.Equal("Product B: QOS/2026", slashed.Name);
        Assert.Equal(Root, Path.GetDirectoryName(slashed.Folder));
        Assert.Equal(2, SourceLibrary.List(Root).Count);

        library.Delete();

        Assert.False(Directory.Exists(library.Folder));
        Assert.Equal("Product B: QOS/2026", Assert.Single(SourceLibrary.List(Root)).Name);
    }

    [Fact]
    [Trait("TC", "TC-SRC-02")]
    public void An_uploaded_folder_keeps_its_structure_every_copy_is_verified_and_the_text_is_read()
    {
        var spec = Pdf(Path.Combine(Originals, "m3", "32s-drug-sub", "32s4-contr-drug-sub", "spec.pdf"),
            ["Specification", "Assay 98.0% - 102.0%", "Total impurities NMT 0.2%"], ["Page two: Water content NMT 0.5% w/w"]);
        var batch = Pdf(Path.Combine(Originals, "m3", "32p-drug-prod", "batch-analysis.pdf"), ["Batch EX-2401 assay 99.6%"]);
        File.WriteAllText(Path.Combine(Originals, "m3", "readme.txt"), "not a PDF");
        File.WriteAllText(Path.Combine(Originals, "m3", ".DS_Store"), "hidden");
        var before = (File.GetLastWriteTimeUtc(spec), SourceLibrary.Fingerprint(spec));
        var library = SourceLibrary.Create("Examplin", Root);
        var progress = new List<SourceProgress>();

        var report = library.UploadFolder(Path.Combine(Originals, "m3"), new NoImageText(), new SynchronousProgress(progress.Add));

        Assert.Equal("2 of 2 PDF files uploaded correctly (2 new, 0 updated, 0 unchanged). 1 file was skipped (not PDF).", report.Summary);
        Assert.Equal(["m3/readme.txt"], report.Skipped);
        Assert.Equal(["m3/32p-drug-prod/batch-analysis.pdf", "m3/32s-drug-sub/32s4-contr-drug-sub/spec.pdf"],
            library.Files.Select(file => file.RelativePath));
        var copy = library.Files.Single(file => file.Name == "spec.pdf");
        Assert.Equal("m3/32s-drug-sub/32s4-contr-drug-sub", copy.Folder);
        Assert.Equal(File.ReadAllBytes(spec), File.ReadAllBytes(library.PathOf(copy)));
        Assert.Equal(SourceLibrary.Fingerprint(spec), copy.Sha256);
        Assert.Equal(spec, copy.OriginalPath);
        Assert.Equal(2, copy.Pages);
        Assert.Equal(0, copy.OcrPages);
        var text = library.TextOf(copy)!;
        Assert.Contains("Total impurities NMT 0.2%", text.Pages[0].Text);
        Assert.Contains("Water content NMT 0.5% w/w", text.Pages[1].Text);
        Assert.Equal(2, text.Pages[1].Number);
        Assert.Equal(new SourceProgress(2, 2, ""), progress[^1]);
        Assert.Equal(Path.Combine(Originals, "m3"), Assert.Single(library.Uploads).OriginalPath);
        // The original is only read.
        Assert.Equal(before, (File.GetLastWriteTimeUtc(spec), SourceLibrary.Fingerprint(spec)));
        // What was uploaded is still there when the source folder is opened again.
        var reopened = SourceLibrary.TryOpen(library.Folder)!;
        Assert.Equal(library.Files, reopened.Files);
        Assert.Contains("Batch EX-2401", reopened.TextOf(reopened.Files[0])!.Pages[0].Text);
    }

    [Fact]
    [Trait("TC", "TC-SRC-03")]
    public void Uploading_the_same_folder_again_brings_the_copy_up_to_date()
    {
        var folder = Path.Combine(Originals, "m3");
        var spec = Pdf(Path.Combine(folder, "32s", "spec.pdf"), ["Total impurities NMT 0.2%"]);
        var stability = Pdf(Path.Combine(folder, "32p8", "stability.pdf"), ["24 months at 25 °C/60% RH"]);
        Pdf(Path.Combine(folder, "32p5", "methods.pdf"), ["AM-101 HPLC"]);
        var library = SourceLibrary.Create("Examplin", Root);
        library.UploadFolder(folder, new NoImageText());
        var removedCopy = library.PathOf(library.Files.Single(file => file.Name == "stability.pdf"));
        var oldSpecHash = library.Files.Single(file => file.Name == "spec.pdf").Sha256;

        Pdf(spec, ["Total impurities NMT 0.15%"]);   // changed
        File.Delete(stability);                         // removed, and its folder is now empty
        Directory.Delete(Path.GetDirectoryName(stability)!);
        Pdf(Path.Combine(folder, "32p5", "validation.pdf"), ["Method validation"]);   // new
        var report = library.UploadFolder(folder, new NoImageText());

        Assert.Equal((1, 1, 1), (report.Added, report.Updated, report.Unchanged));
        Assert.Equal(["m3/32p8/stability.pdf"], report.Removed);
        Assert.Contains("1 removed because they are no longer in your folder", report.Summary);
        Assert.Equal(["m3/32p5/methods.pdf", "m3/32p5/validation.pdf", "m3/32s/spec.pdf"], library.Files.Select(file => file.RelativePath));
        Assert.False(File.Exists(removedCopy));
        Assert.False(Directory.Exists(Path.GetDirectoryName(removedCopy)));   // the copy has the same structure as the original
        var updated = library.Files.Single(file => file.Name == "spec.pdf");
        Assert.Contains("NMT 0.15%", library.TextOf(updated)!.Pages[0].Text);
        Assert.False(File.Exists(Path.Combine(library.Folder, "text", oldSpecHash + ".json")));   // old text not kept
        Assert.Single(library.Uploads);

        // "Update from originals" does the same for every uploaded folder.
        Pdf(Path.Combine(folder, "32p5", "methods.pdf"), ["AM-101 HPLC, revised"]);
        var (upload, again) = Assert.Single(library.UpdateFromOriginals(new NoImageText()));
        Assert.Equal("m3", upload.Name);
        Assert.Equal((0, 1, 2), (again!.Added, again.Updated, again.Unchanged));
    }

    [Fact]
    [Trait("TC", "TC-SRC-04")]
    public void Pdfs_that_cannot_be_read_are_kept_and_listed_with_the_reason()
    {
        var folder = Path.Combine(Originals, "sources");
        Pdf(Path.Combine(folder, "locked.pdf"), ["Confidential"], password: "secret");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "damaged.pdf"), "this is not a PDF");
        Pdf(Path.Combine(folder, "fine.pdf"), ["Assay 99.6%"]);
        var library = SourceLibrary.Create("Mixed", Root);

        var report = library.UploadFolder(folder, new NoImageText());

        Assert.Equal(3, report.Verified);
        Assert.Contains("2 cannot be read", report.Summary);
        Assert.Equal("The PDF is password-protected.", library.Files.Single(file => file.Name == "locked.pdf").Problem);
        Assert.Contains("damaged or not a PDF", library.Files.Single(file => file.Name == "damaged.pdf").Problem);
        Assert.True(library.Files.Single(file => file.Name == "fine.pdf").IsReadable);
        Assert.Null(library.TextOf(library.Files.Single(file => file.Name == "locked.pdf")));
    }

    [Fact]
    [Trait("TC", "TC-SRC-05")]
    public void Scanned_pages_are_read_with_text_recognition_and_text_pages_are_not()
    {
        var folder = Path.Combine(Originals, "scans");
        Scan(Path.Combine(folder, "scan.pdf"), "Assay 98.7%");
        Pdf(Path.Combine(folder, "text.pdf"), ["Assay 99.6%"]);
        var ocr = new GivenWords("Assay", "98.7%");
        var library = SourceLibrary.Create("Scans", Root);

        library.UploadFolder(folder, ocr);

        var scan = library.Files.Single(file => file.Name == "scan.pdf");
        Assert.Equal((1, 1, 0), (scan.Pages, scan.OcrPages, scan.ScannedPagesNotRead));
        var page = Assert.Single(library.TextOf(scan)!.Pages);
        Assert.True(page.Ocr);
        Assert.Equal("Assay 98.7%", page.Text);
        Assert.Equal(1, ocr.Calls);   // the page with a text layer was not sent to text recognition
        Assert.Equal(0, library.Files.Single(file => file.Name == "text.pdf").OcrPages);

        // Without text recognition the scanned page is counted as not read, and the file is still uploaded.
        var other = SourceLibrary.Create("Scans without OCR", Root);
        other.UploadFolder(folder, new NoImageText());
        Assert.Equal(1, other.Files.Single(file => file.Name == "scan.pdf").ScannedPagesNotRead);
    }

    [OcrFact]
    [Trait("TC", "TC-SRC-06")]
    public void A_scanned_page_is_read_by_the_computers_own_text_recognition()
    {
        var folder = Path.Combine(Originals, "scans");
        Scan(Path.Combine(folder, "scan.pdf"), "Total impurities NMT 0.2%");
        var library = SourceLibrary.Create("Real OCR", Root);

        library.UploadFolder(folder, new SystemImageTextReader());

        var scan = Assert.Single(library.Files);
        Assert.Equal(1, scan.OcrPages);
        Assert.Contains("impurities", library.TextOf(scan)!.Pages[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.2%", library.TextOf(scan)!.Pages[0].Text.Replace(" ", ""));
    }

    [Fact]
    [Trait("TC", "TC-SRC-07")]
    public void Single_pdfs_are_added_at_the_top_and_the_tool_will_not_upload_its_own_folders()
    {
        var first = Pdf(Path.Combine(Originals, "a", "certificate.pdf"), ["CoA EX-2401"]);
        var notes = Path.Combine(Originals, "a", "notes.docx");
        File.WriteAllText(notes, "x");
        var library = SourceLibrary.Create("Single files", Root);

        var report = library.AddFiles([first, notes], new NoImageText());

        Assert.Equal("certificate.pdf", Assert.Single(library.Files).RelativePath);
        Assert.Equal(["notes.docx"], report.Skipped);
        Assert.Equal(1, library.AddFiles([first], new NoImageText()).Unchanged);
        Assert.Throws<ArgumentException>(() => library.UploadFolder(Root, new NoImageText()));
        Assert.Throws<ArgumentException>(() => library.UploadFolder(library.Folder, new NoImageText()));
        Assert.Throws<ArgumentException>(() => library.UploadFolder(_directory, new NoImageText()));   // contains the source folders

        library.Remove("certificate.pdf");
        Assert.Empty(library.Files);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(library.Folder, "text")));
    }

    [Fact]
    [Trait("TC", "TC-SRC-05")]
    public void Recognised_words_are_put_back_into_lines()
    {
        static ImageTextWord Word(string text, int x, int y) => new(text, new ImageDifferenceRegion(x, y, 40, 20));

        var text = SourceTextReader.LinesOf([Word("98.7%", 200, 52), Word("Total", 10, 100), Word("Assay", 10, 50), Word("0.2%", 200, 98)]);

        Assert.Equal("Assay 98.7%\nTotal 0.2%", text);
    }

    /// <summary>
    /// The Source folders window, inside the shared UI test (Avalonia can only be set up once per process): a folder
    /// is created, a nested folder uploaded, and the tree shows the same structure with how each PDF was read.
    /// </summary>
    internal static void VerifyWindow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DocumentChecker.SourcesUi.{Guid.NewGuid():N}");
        try
        {
            var originals = Path.Combine(directory, "Originals", "m3");
            Pdf(Path.Combine(originals, "32s-drug-sub", "spec.pdf"), ["Total impurities NMT 0.2%"], ["Water NMT 0.5%"]);
            Pdf(Path.Combine(originals, "32s-drug-sub", "locked.pdf"), ["Confidential"], password: "secret");
            Pdf(Path.Combine(originals, "32p-drug-prod", "batch-analysis.pdf"), ["Batch EX-2401"]);
            File.WriteAllText(Path.Combine(originals, "notes.txt"), "not a PDF");
            var window = new SourceLibraryWindow(Path.Combine(directory, "Sources"));
            window.Show();
            try
            {
                Assert.Equal(0, window.ListedCount);
                Assert.NotNull(window.CreateLibrary(" "));
                Assert.Null(window.CreateLibrary("Examplin 250 mg – Module 3"));
                Assert.Equal(1, window.ListedCount);
                Assert.Contains("No PDFs yet", window.SummaryText);

                var upload = window.UploadFolderAsync(originals);
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!upload.IsCompleted)
                {
                    Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the upload.");
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(20);
                }
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Assert.StartsWith("3 of 3 PDF files uploaded correctly (3 new, 0 updated, 0 unchanged).", window.StatusText);
                Assert.Contains("1 cannot be read", window.StatusText);
                Assert.Contains("1 file was skipped (not PDF)", window.StatusText);
                Assert.Equal(string.Join("\n",
                    "m3 · 3 PDFs",
                    "  32p-drug-prod · 1 PDF",
                    "    batch-analysis.pdf · 1 page",
                    "  32s-drug-sub · 2 PDFs",
                    "    locked.pdf · ⚠ The PDF is password-protected.",
                    "    spec.pdf · 2 pages"), window.TreeText);
                Assert.Contains("3 PDFs, 3 pages", window.SummaryText);
                Assert.Contains("⚠ 1 PDF cannot be read", window.SummaryText);
                Assert.Contains("uploaded from " + originals, window.SummaryText);

                window.SelectFile("m3/32s-drug-sub/spec.pdf");
                Assert.Contains("m3/32s-drug-sub", window.FileDetailsText);
                Assert.Contains("copy verified", window.FileDetailsText);
                Assert.Contains(SourceLibrary.Fingerprint(Path.Combine(originals, "32s-drug-sub", "spec.pdf")), window.FileDetailsText);

                if (Environment.GetEnvironmentVariable("DOCHECK_SOURCES_CAPTURE") is { Length: > 0 } capture)
                {
                    window.Measure(new Avalonia.Size(1180, 780));
                    window.Arrange(new Avalonia.Rect(0, 0, 1180, 780));
                    window.UpdateLayout();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1180, 780), new Avalonia.Vector(96, 96));
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

    // ---- synthetic PDFs ----

    private static string Pdf(string path, params string[][] pages) => Pdf(path, pages, password: null);

    private static string Pdf(string path, string[] lines, string? password = null) => Pdf(path, [lines], password);

    /// <summary>A PDF with a text layer, one array of lines per page; a tab moves to the next column, as in a table.</summary>
    internal static string Pdf(string path, string[][] pages, string? password)
    {
        UseFonts();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var document = new PdfDocument();
        foreach (var lines in pages)
        {
            var page = document.AddPage();
            using var graphics = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 12);
            for (var index = 0; index < lines.Length; index++)
            {
                var cells = lines[index].Split('\t');
                for (var column = 0; column < cells.Length; column++)
                    graphics.DrawString(cells[column], font, XBrushes.Black, new XPoint(new[] { 50, 270, 450 }[Math.Min(column, 2)], 80 + index * 22));
            }
        }
        if (password is not null)
            document.SecuritySettings.UserPassword = password;
        document.Save(path);
        return path;
    }

    /// <summary>A "scanned" PDF: a page drawn as a picture, so it has no text layer.</summary>
    private void Scan(string path, string text)
    {
        var printed = Pdf(Path.Combine(_directory, "printed-" + Guid.NewGuid().ToString("N") + ".pdf"), [new[] { text }], password: null);
        var picture = Pdfium.ReadPage(File.ReadAllBytes(printed), 0, scale: 3);
        using var image = Image.LoadPixelData<Bgra32>(picture.Pixels, picture.PixelWidth, picture.PixelHeight);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        png.Position = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var document = new PdfDocument();
        var page = document.AddPage();
        using (var graphics = XGraphics.FromPdfPage(page))
            graphics.DrawImage(XImage.FromStream(png), 0, 0, page.Width.Point, page.Height.Point);
        document.Save(path);
    }

    private static void UseFonts()
    {
        lock (FontGate)
        {
            try
            {
                GlobalFontSettings.FontResolver ??= new LocalFontResolver();
            }
            catch (InvalidOperationException) { }   // already set and in use by another test
        }
    }

    private sealed class NoImageText : IImageTextReader
    {
        public ImageTextResult Read(byte[] imageData) => new([], "Text recognition is not used in this test.");
    }

    private sealed class GivenWords(params string[] words) : IImageTextReader
    {
        public int Calls { get; private set; }

        public ImageTextResult Read(byte[] imageData)
        {
            Calls++;
            return new(words.Select((word, index) => new ImageTextWord(word, new ImageDifferenceRegion(100 + index * 120, 200, 100, 30))).ToList());
        }
    }

    private sealed class SynchronousProgress(Action<SourceProgress> report) : IProgress<SourceProgress>
    {
        public void Report(SourceProgress value) => report(value);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
            return;
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_directory, recursive: true);
    }
}
