using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.ExactView;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DocumentChecker.Engine.Sources;

/// <summary>The text of one page of a source PDF, and whether it was read by text recognition (a scanned page).</summary>
public sealed record SourcePageText(int Number, string Text, bool Ocr);

/// <summary>The text of a source PDF, page by page.</summary>
public sealed record SourceText(IReadOnlyList<SourcePageText> Pages, int ScannedPagesNotRead = 0, string? OcrProblem = null);

/// <summary>
/// Reads the text of a source PDF. Pages with a text layer are read with PDFium. A page with (almost) no text but a
/// picture is a scan: it is drawn and read with the computer's own text recognition.
/// </summary>
public sealed class SourceTextReader(IImageTextReader imageTextReader)
{
    // A page with fewer printed characters than this, and a picture on it, is treated as a scan.
    private const int ScanThreshold = 10;
    // Scanned pages are drawn at 216 dpi (3 pixels per point) for text recognition.
    private const double OcrScale = 3;

    public SourceText Read(byte[] pdf)
    {
        var pages = Pdfium.ReadText(pdf);
        var result = new List<SourcePageText>();
        var notRead = 0;
        string? ocrProblem = null;
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            var printed = page.Characters.Count(character => !char.IsWhiteSpace(character.Character));
            if (printed >= ScanThreshold || page.PictureCount == 0)
            {
                result.Add(new SourcePageText(index + 1, TextOf(page.Characters), Ocr: false));
                continue;
            }

            var words = ReadScannedPage(pdf, index, out var problem);
            if (problem is not null)
            {
                notRead++;
                ocrProblem ??= problem;
                result.Add(new SourcePageText(index + 1, TextOf(page.Characters), Ocr: false));
            }
            else
                result.Add(new SourcePageText(index + 1, words, Ocr: true));
        }
        return new SourceText(result, notRead, ocrProblem);
    }

    /// <summary>The page text as PDFium gives it (it adds the spaces and line breaks), with Windows line ends made plain.</summary>
    private static string TextOf(IReadOnlyList<PdfCharacter> characters) => TextWithMap(characters).Text;

    /// <summary>
    /// The page text, and for each of its characters the index of the PDF character it came from, so a stretch of the
    /// stored text can be boxed on the drawn page.
    /// </summary>
    internal static (string Text, int[] Map) TextWithMap(IReadOnlyList<PdfCharacter> characters)
    {
        var text = new System.Text.StringBuilder(characters.Count);
        var map = new List<int>(characters.Count);
        for (var index = 0; index < characters.Count; index++)
        {
            var character = characters[index].Character;
            if (character == '\r' && index + 1 < characters.Count && characters[index + 1].Character == '\n')
                continue;
            text.Append(character == '\r' ? '\n' : character);
            map.Add(index);
        }
        var full = text.ToString();
        var start = full.Length - full.TrimStart().Length;
        var trimmed = full.Trim();
        return (trimmed, map.Skip(start).Take(trimmed.Length).ToArray());
    }

    private string ReadScannedPage(byte[] pdf, int index, out string? problem)
    {
        var page = Pdfium.ReadPage(pdf, index, OcrScale);
        using var image = Image.LoadPixelData<Bgra32>(page.Pixels, page.PixelWidth, page.PixelHeight);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        var result = imageTextReader.Read(png.ToArray());
        problem = result.Error is { } error && result.Words.Count == 0 ? error : null;
        return LinesOf(result.Words);
    }

    /// <summary>Recognised words put back into lines: words whose middles are at about the same height share a line.</summary>
    internal static string LinesOf(IReadOnlyList<ImageTextWord> words)
    {
        var lines = new List<List<ImageTextWord>>();
        foreach (var word in words.OrderBy(word => word.Bounds.Y + word.Bounds.Height / 2.0))
        {
            var middle = word.Bounds.Y + word.Bounds.Height / 2.0;
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs(middle - line.Average(other => other.Bounds.Y + other.Bounds.Height / 2.0)) <= word.Bounds.Height * 0.5)
                line.Add(word);
            else
                lines.Add([word]);
        }
        return string.Join("\n", lines.Select(line => string.Join(" ", line.OrderBy(word => word.Bounds.X).Select(word => word.Text))));
    }
}
