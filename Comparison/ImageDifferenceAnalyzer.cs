using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DocumentChecker.Engine.Comparison;

internal static class ImageDifferenceAnalyzer
{
    public static IReadOnlyList<ImageDifferenceRegion> FindChangedRegions(byte[] referenceData, byte[] currentData)
    {
        try
        {
            using var reference = Image.Load<Rgba32>(referenceData);
            using var current = Image.Load<Rgba32>(currentData);
            if (reference.Width != current.Width || reference.Height != current.Height)
                return [new ImageDifferenceRegion(0, 0, Math.Max(reference.Width, current.Width), Math.Max(reference.Height, current.Height))];
            var width = Math.Min(reference.Width, current.Width);
            var height = Math.Min(reference.Height, current.Height);
            if (width == 0 || height == 0)
                return [];

            var step = Math.Max(1, Math.Max(width, height) / 1200);
            var columns = (width + step - 1) / step;
            var rows = (height + step - 1) / step;
            var changed = new bool[rows, columns];
            reference.ProcessPixelRows(current, (referencePixels, currentPixels) =>
            {
                for (var y = 0; y < height; y += step)
                {
                    var referenceRow = referencePixels.GetRowSpan(y);
                    var currentRow = currentPixels.GetRowSpan(y);
                    for (var x = 0; x < width; x += step)
                        changed[y / step, x / step] = PixelChanged(referenceRow[x], currentRow[x]);
                }
            });

            var regions = new List<ImageDifferenceRegion>();
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                if (!changed[row, column])
                    continue;

                regions.Add(ReadRegion(changed, row, column, step, width, height));
            }

            return MergeNearby(regions.Where(region => region.Width * region.Height >= step * step).ToList(), Math.Max(8, step * 4));
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Joins regions that are close together, so one changed value whose characters are separated by unchanged
    /// pixels (99.1 -> 98.4) is one region and one finding. Side by side, "close" scales with the text height
    /// (about three quarters of a character); above and below, it stays at <paramref name="gap"/> pixels so separate
    /// lines of text stay separate.
    /// </summary>
    private static List<ImageDifferenceRegion> MergeNearby(List<ImageDifferenceRegion> regions, int gap)
    {
        for (var merged = true; merged;)
        {
            merged = false;
            for (var first = 0; first < regions.Count && !merged; first++)
            for (var second = first + 1; second < regions.Count && !merged; second++)
            {
                var a = regions[first];
                var b = regions[second];
                var sideGap = Math.Clamp((int)(0.75 * Math.Max(a.Height, b.Height)), gap, gap * 4);
                if (a.X - sideGap >= b.X + b.Width || b.X - sideGap >= a.X + a.Width || a.Y - gap >= b.Y + b.Height || b.Y - gap >= a.Y + a.Height)
                    continue;
                var x = Math.Min(a.X, b.X);
                var y = Math.Min(a.Y, b.Y);
                regions[first] = new ImageDifferenceRegion(x, y,
                    Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
                regions.RemoveAt(second);
                merged = true;
            }
        }
        return regions;
    }

    private static ImageDifferenceRegion ReadRegion(bool[,] changed, int startRow, int startColumn, int step, int width, int height)
    {
        var queue = new Queue<(int Row, int Column)>();
        queue.Enqueue((startRow, startColumn));
        changed[startRow, startColumn] = false;
        var minRow = startRow; var maxRow = startRow; var minColumn = startColumn; var maxColumn = startColumn;
        while (queue.Count > 0)
        {
            var (row, column) = queue.Dequeue();
            minRow = Math.Min(minRow, row); maxRow = Math.Max(maxRow, row);
            minColumn = Math.Min(minColumn, column); maxColumn = Math.Max(maxColumn, column);
            for (var rowOffset = -1; rowOffset <= 1; rowOffset++)
            for (var columnOffset = -1; columnOffset <= 1; columnOffset++)
            {
                var nextRow = row + rowOffset; var nextColumn = column + columnOffset;
                if (nextRow < 0 || nextColumn < 0 || nextRow >= changed.GetLength(0) || nextColumn >= changed.GetLength(1) || !changed[nextRow, nextColumn])
                    continue;
                changed[nextRow, nextColumn] = false;
                queue.Enqueue((nextRow, nextColumn));
            }
        }
        return new ImageDifferenceRegion(minColumn * step, minRow * step,
            Math.Min(width, (maxColumn + 1) * step) - minColumn * step,
            Math.Min(height, (maxRow + 1) * step) - minRow * step);
    }

    private static bool PixelChanged(Rgba32 left, Rgba32 right) =>
        Math.Abs(left.R - right.R) > 24 || Math.Abs(left.G - right.G) > 24 ||
        Math.Abs(left.B - right.B) > 24 || Math.Abs(left.A - right.A) > 24;
}
