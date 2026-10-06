using System.Text.RegularExpressions;
using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.Comparison;

public sealed record RecognizedTableCell(DocumentTableCell Cell, string Text, ImageDifferenceRegion Bounds);
public sealed record RecognizedTableRows(IReadOnlyList<RecognizedTableCell> Cells, int UnresolvedRows);

public static class ImageTableRows
{
    /// <summary>Read simple visual rows by their unique row label, then by column gaps. Never guess an unresolved grid.</summary>
    public static RecognizedTableRows Read(IReadOnlyList<ImageTextWord> words, IReadOnlyList<TableDisplayBlock> tables)
    {
        var output = new List<RecognizedTableCell>();
        var rows = tables.SelectMany(t => t.Rows).ToList();
        var keys = rows.Select(row => Key(CellText(row.Cells.FirstOrDefault()))).ToList();
        var unresolved = 0;
        foreach (var row in rows)
        {
            var key = Key(CellText(row.Cells.FirstOrDefault()));
            var anchors = words.Where(w => Key(w.Text) == key).ToList();
            if (key.Length == 0 || keys.Count(k => k == key) != 1 || anchors.Count != 1 || row.Cells.Count < 2)
            { unresolved++; continue; }
            var anchor = anchors[0];
            var band = words.Where(w => Math.Abs(CenterY(w.Bounds) - CenterY(anchor.Bounds)) <= Math.Max(w.Bounds.Height, anchor.Bounds.Height) * .65)
                .OrderBy(w => w.Bounds.X).ToList();
            if (band.Count < row.Cells.Count || !ReferenceEquals(band[0], anchor))
            { unresolved++; continue; }
            var cuts = Enumerable.Range(1, band.Count - 1)
                .Select(i => (Index: i, Gap: band[i].Bounds.X - (band[i - 1].Bounds.X + band[i - 1].Bounds.Width)))
                .OrderByDescending(item => item.Gap).Take(row.Cells.Count - 1).ToList();
            var characterWidth = band.Average(w => (double)w.Bounds.Width / Math.Max(1, w.Text.Length));
            if (cuts.Count != row.Cells.Count - 1 || cuts.Any(cut => cut.Gap < characterWidth * 1.8))
            { unresolved++; continue; }
            var bounds = cuts.Select(c => c.Index).Append(0).Append(band.Count).Order().ToList();
            for (var i = 0; i < row.Cells.Count; i++)
            {
                var cellWords = band.Skip(bounds[i]).Take(bounds[i + 1] - bounds[i]).ToList();
                var x = cellWords.Min(w => w.Bounds.X); var y = cellWords.Min(w => w.Bounds.Y);
                output.Add(new(row.Cells[i], string.Join(" ", cellWords.Select(w => w.Text)),
                    new(x, y, cellWords.Max(w => w.Bounds.X + w.Bounds.Width) - x, cellWords.Max(w => w.Bounds.Y + w.Bounds.Height) - y)));
            }
        }
        return new(output, unresolved);
    }

    public static string CellText(DocumentTableCell? cell) => cell is null ? "" : string.Join(" ", cell.Paragraphs.Select(p => p.Text));
    private static string Key(string text) => Regex.Match(text, @"[\p{L}\p{N}]+").Value;
    private static double CenterY(ImageDifferenceRegion bounds) => bounds.Y + bounds.Height / 2d;
}
