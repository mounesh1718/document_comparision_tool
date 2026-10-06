using DiffPlex;

namespace DocumentChecker.Engine.Comparison;

public sealed record ImageContentChange(string Reference, string Current, ImageDifferenceRegion? ReferenceRegion,
    ImageDifferenceRegion? CurrentRegion, string Context, bool IsText, string? ReviewNote = null);

public static class ImageContentComparer
{
    public static IReadOnlyList<ImageContentChange> Compare(ImageTextResult reference, ImageTextResult current,
        IReadOnlyList<ImageDifferenceRegion> visualRegions)
    {
        var changes = new List<ImageContentChange>();
        var chartWords = CompareChartLabels(reference.Words, current.Words, visualRegions, changes);
        var leftRows = Rows(reference.Words.Where(word => !chartWords.Left.Contains(word)).ToList());
        var rightRows = Rows(current.Words.Where(word => !chartWords.Right.Contains(word)).ToList());
        var used = new HashSet<int>();
        foreach (var left in leftRows)
        {
            var bounds = Bounds(left);
            var match = Enumerable.Range(0, rightRows.Count).Where(index => !used.Contains(index))
                .Select(index => (Index: index, Distance: Math.Abs(CenterY(Bounds(rightRows[index])) - CenterY(bounds))))
                .Where(item => item.Distance <= Math.Max(bounds.Height, Bounds(rightRows[item.Index]).Height))
                .OrderBy(item => item.Distance).FirstOrDefault((Index: -1, Distance: 0d));
            var right = match.Index < 0 ? [] : rightRows[match.Index];
            if (match.Index >= 0)
                used.Add(match.Index);
            CompareRow(left, right, visualRegions, changes);
        }
        for (var index = 0; index < rightRows.Count; index++)
            if (!used.Contains(index))
                CompareRow([], rightRows[index], visualRegions, changes);

        foreach (var region in visualRegions)
        {
            if (changes.Any(change => Intersects(change.ReferenceRegion, region) || Intersects(change.CurrentRegion, region)))
                continue;
            changes.Add(new ImageContentChange("Reference image area", "Current image area", region, region,
                "Visual change — compare the enlarged areas below.", false,
                reference.Error ?? current.Error ?? "No readable text change was recognized here. Review the image crops."));
        }
        return changes.OrderBy(change => (change.ReferenceRegion ?? change.CurrentRegion)?.Y)
            .ThenBy(change => (change.ReferenceRegion ?? change.CurrentRegion)?.X).ToList();
    }

    private static (HashSet<ImageTextWord> Left, HashSet<ImageTextWord> Right) CompareChartLabels(
        IReadOnlyList<ImageTextWord> left, IReadOnlyList<ImageTextWord> right,
        IReadOnlyList<ImageDifferenceRegion> visualRegions, List<ImageContentChange> changes)
    {
        var usedLeft = new HashSet<ImageTextWord>();
        var usedRight = new HashSet<ImageTextWord>();
        // Recognize only an unambiguous category axis: at least three alphabetic labels
        // on the bottom row, with a single numeric label directly above each category.
        var axis = Rows(left).LastOrDefault();
        var otherAxis = Rows(right).LastOrDefault();
        if (axis is null || otherAxis is null || axis.Count < 3 || axis.Count != otherAxis.Count ||
            axis.Any(word => !word.Text.All(char.IsLetter)) ||
            !axis.Select(word => word.Text).SequenceEqual(otherAxis.Select(word => word.Text)))
            return (usedLeft, usedRight);
        var pairs = new List<(ImageTextWord Label, ImageTextWord Before, ImageTextWord After)>();
        for (var index = 0; index < axis.Count; index++)
        {
            var spacing = axis.Where((_, i) => i != index).Min(word => Math.Abs(CenterX(word.Bounds) - CenterX(axis[index].Bounds)));
            var before = FindValue(left, axis[index], spacing / 3);
            var after = FindValue(right, otherAxis[index], spacing / 3);
            if (before is null || after is null)
                return (usedLeft, usedRight);
            pairs.Add((axis[index], before, after));
        }
        foreach (var (label, before, after) in pairs)
        {
            usedLeft.Add(before);
            usedRight.Add(after);
            if (before.Text != after.Text && visualRegions.Any(region => Intersects(before.Bounds, region) || Intersects(after.Bounds, region)))
                changes.Add(new(before.Text, after.Text, before.Bounds, after.Bounds,
                    $"Chart category: {label.Text}", true, "Chart label recognized from an image. Verify it against the enlarged crops."));
        }
        return (usedLeft, usedRight);

        static ImageTextWord? FindValue(IReadOnlyList<ImageTextWord> words, ImageTextWord label, double tolerance)
        {
            var candidates = words.Where(word => word.Bounds.Y + word.Bounds.Height < label.Bounds.Y &&
                Math.Abs(CenterX(word.Bounds) - CenterX(label.Bounds)) < tolerance &&
                word.Text.Any(char.IsDigit) && word.Text.All(character => char.IsDigit(character) || ".,%+-".Contains(character))).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }
    }

    private static void CompareRow(List<ImageTextWord> left, List<ImageTextWord> right,
        IReadOnlyList<ImageDifferenceRegion> visualRegions, List<ImageContentChange> changes)
    {
        var diff = new Differ().CreateLineDiffs(string.Join('\n', left.Select(word => word.Text)),
            string.Join('\n', right.Select(word => word.Text)), false, false);
        foreach (var block in diff.DiffBlocks)
        {
            // Separate adjacent table values, even when both changed in the same row.
            if (block.DeleteCountA == block.InsertCountB)
            {
                for (var index = 0; index < block.DeleteCountA; index++)
                    Add([left[block.DeleteStartA + index]], [right[block.InsertStartB + index]]);
            }
            else
                Add(left.Skip(block.DeleteStartA).Take(block.DeleteCountA).ToList(),
                    right.Skip(block.InsertStartB).Take(block.InsertCountB).ToList());
        }

        void Add(List<ImageTextWord> before, List<ImageTextWord> after)
        {
            var beforeBounds = before.Count == 0 ? null : Bounds(before);
            var afterBounds = after.Count == 0 ? null : Bounds(after);
            // OCR can produce inconsistent recognition for identical pixels. Require visual evidence.
            if (!visualRegions.Any(region => Intersects(beforeBounds, region) || Intersects(afterBounds, region)))
                return;
            changes.Add(new ImageContentChange(
                before.Count == 0 ? "(no corresponding content)" : string.Join(' ', before.Select(word => word.Text)),
                after.Count == 0 ? "(no corresponding content)" : string.Join(' ', after.Select(word => word.Text)),
                beforeBounds, afterBounds,
                $"Image row: {string.Join(' ', left.Select(word => word.Text))}\nCurrent row: {string.Join(' ', right.Select(word => word.Text))}",
                true, "Text recognized from an image. Verify the value against the enlarged crops."));
        }
    }

    private static List<List<ImageTextWord>> Rows(IReadOnlyList<ImageTextWord> words)
    {
        var rows = new List<List<ImageTextWord>>();
        foreach (var word in words.OrderBy(word => CenterY(word.Bounds)).ThenBy(word => word.Bounds.X))
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(CenterY(Bounds(row)) - CenterY(word.Bounds)) > Math.Max(Bounds(row).Height, word.Bounds.Height) * 0.55)
            {
                row = [];
                rows.Add(row);
            }
            row.Add(word);
        }
        return rows.Select(row => row.OrderBy(word => word.Bounds.X).ToList()).ToList();
    }

    private static double CenterY(ImageDifferenceRegion region) => region.Y + region.Height / 2d;
    private static double CenterX(ImageDifferenceRegion region) => region.X + region.Width / 2d;

    public static bool Intersects(ImageDifferenceRegion? left, ImageDifferenceRegion right) => left is not null &&
        left.X < right.X + right.Width && left.X + left.Width > right.X &&
        left.Y < right.Y + right.Height && left.Y + left.Height > right.Y;

    private static ImageDifferenceRegion Bounds(IReadOnlyList<ImageTextWord> words)
    {
        var x = words.Min(word => word.Bounds.X);
        var y = words.Min(word => word.Bounds.Y);
        return new ImageDifferenceRegion(x, y, words.Max(word => word.Bounds.X + word.Bounds.Width) - x,
            words.Max(word => word.Bounds.Y + word.Bounds.Height) - y);
    }
}
