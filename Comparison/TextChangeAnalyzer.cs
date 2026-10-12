using DiffPlex;

namespace DocumentChecker.Engine.Comparison;

public sealed record TextChangeSpan(int ReferenceStart, int ReferenceLength, int CurrentStart, int CurrentLength);

public static class TextChangeAnalyzer
{
    public static IReadOnlyList<TextChangeSpan> Find(string reference, string current)
    {
        var changes = new List<TextChangeSpan>();
        foreach (var block in new Differ().CreateCharacterDiffs(reference, current, false, false).DiffBlocks)
        {
            var (leftStart, leftEnd) = Expand(reference, block.DeleteStartA, block.DeleteCountA);
            var (rightStart, rightEnd) = Expand(current, block.InsertStartB, block.InsertCountB);
            if (block.DeleteCountA > 0 && block.InsertCountB > 0 &&
                (string.IsNullOrWhiteSpace(reference.Substring(block.DeleteStartA, block.DeleteCountA)) ||
                 string.IsNullOrWhiteSpace(current.Substring(block.InsertStartB, block.InsertCountB))))
            {
                // A punctuation/space replacement must not claim the surrounding word was replaced by a space.
                leftStart = block.DeleteStartA; leftEnd = leftStart + block.DeleteCountA;
                rightStart = block.InsertStartB; rightEnd = rightStart + block.InsertCountB;
            }
            // A suffix insertion/deletion belongs to its whole value on both sides (e.g. MD-4482 -> MD-4482-R).
            if (block.DeleteCountA > 0 && block.InsertCountB == 0 &&
                !reference.Substring(block.DeleteStartA, block.DeleteCountA).Any(char.IsWhiteSpace))
                (rightStart, rightEnd) = ExpandAt(current, rightStart);
            if (block.InsertCountB > 0 && block.DeleteCountA == 0 &&
                !current.Substring(block.InsertStartB, block.InsertCountB).Any(char.IsWhiteSpace))
                (leftStart, leftEnd) = ExpandAt(reference, leftStart);
            // Overlapping changes are one change, and so are changes that touch on both sides with nothing
            // unchanged between them (e.g. "Confidential" -> "Internal Use" is one replacement, not two).
            if (changes.LastOrDefault() is { } previous &&
                (leftStart < previous.ReferenceStart + previous.ReferenceLength ||
                rightStart < previous.CurrentStart + previous.CurrentLength ||
                leftStart <= previous.ReferenceStart + previous.ReferenceLength &&
                rightStart <= previous.CurrentStart + previous.CurrentLength))
            {
                var a = Math.Min(leftStart, previous.ReferenceStart); var b = Math.Min(rightStart, previous.CurrentStart);
                changes[^1] = new(a, Math.Max(leftEnd, previous.ReferenceStart + previous.ReferenceLength) - a,
                    b, Math.Max(rightEnd, previous.CurrentStart + previous.CurrentLength) - b);
            }
            else
                changes.Add(new(leftStart, leftEnd - leftStart, rightStart, rightEnd - rightStart));
        }
        return changes;
    }

    private static (int Start, int End) ExpandAt(string value, int position)
    {
        if (position < value.Length && !char.IsWhiteSpace(value[position]))
            return Expand(value, position, 1);
        if (position > 0 && !char.IsWhiteSpace(value[position - 1]))
            return Expand(value, position - 1, 1);
        return (position, position);
    }

    private static (int Start, int End) Expand(string value, int start, int length)
    {
        var end = start + length;
        if (length == 0 || string.IsNullOrWhiteSpace(value.Substring(start, length)))
            return (start, end);
        while (start > 0 && !char.IsWhiteSpace(value[start - 1]) && start < value.Length && !char.IsWhiteSpace(value[start]))
            start--;
        while (end < value.Length && end > 0 && !char.IsWhiteSpace(value[end]) && !char.IsWhiteSpace(value[end - 1]))
            end++;
        return (start, end);
    }
}
