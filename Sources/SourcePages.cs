using DocumentChecker.Engine.ExactView;

namespace DocumentChecker.Engine.Sources;

/// <summary>Draws the page of a source PDF that holds the evidence for a fact, with boxes round the evidence.</summary>
public static class SourcePages
{
    /// <summary>
    /// The page and one box per printed line of the evidence, in points from the top-left of the page. Scanned pages
    /// (read by text recognition) are shown without boxes.
    /// </summary>
    public static (PdfPage Page, IReadOnlyList<PageRect> Boxes) Draw(string pdfPath, SourceEvidence evidence, double scale = 2)
    {
        var page = Pdfium.ReadPage(File.ReadAllBytes(pdfPath), evidence.Page - 1, scale);
        if (evidence.Ocr || evidence.Length == 0)
            return (page, []);
        var (_, map) = SourceTextReader.TextWithMap(page.Characters);
        var end = Math.Min(map.Length, evidence.Start + evidence.Length);
        var printed = Enumerable.Range(evidence.Start, Math.Max(0, end - evidence.Start))
            .Select(index => page.Characters[map[index]])
            .Where(character => !char.IsWhiteSpace(character.Character) && character.Width > 0 && character.Height > 0)
            .ToList();
        var boxes = new List<PageRect>();
        foreach (var line in Lines(printed))
        {
            var left = line.Min(character => character.X);
            var top = line.Min(character => character.Y);
            boxes.Add(new PageRect(evidence.Page - 1, left, top, line.Max(character => character.X + character.Width) - left,
                line.Max(character => character.Y + character.Height) - top));
        }
        return (page, boxes);
    }

    /// <summary>Characters whose middles are at about the same height are one printed line.</summary>
    private static IEnumerable<List<PdfCharacter>> Lines(IEnumerable<PdfCharacter> characters)
    {
        List<PdfCharacter>? line = null;
        foreach (var character in characters)
        {
            var middle = character.Y + character.Height / 2;
            if (line is not null && Math.Abs(middle - (line[^1].Y + line[^1].Height / 2)) <= Math.Max(character.Height, line[^1].Height) * 0.6)
                line.Add(character);
            else
            {
                if (line is not null)
                    yield return line;
                line = [character];
            }
        }
        if (line is not null)
            yield return line;
    }
}
