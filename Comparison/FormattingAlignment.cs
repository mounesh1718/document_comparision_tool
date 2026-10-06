using DiffPlex;
using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.Comparison;

public sealed record AlignedFormattingSpan(TextRun ReferenceRun, TextRun CurrentRun, int ReferenceStart, int CurrentStart, int Length);

/// <summary>Align character positions before comparing formatting; Word run boundaries are storage details.</summary>
public static class FormattingAlignment
{
    public static IEnumerable<AlignedFormattingSpan> Align(ParagraphBlock reference, ParagraphBlock current)
    {
        var leftRuns = Runs(reference); var rightRuns = Runs(current);
        var left = 0; var right = 0;
        foreach (var change in new Differ().CreateCharacterDiffs(reference.Text, current.Text, false, false).DiffBlocks)
        {
            foreach (var span in Split(left, right, change.DeleteStartA - left)) yield return span;
            // Replacements can have both text and formatting changes. Insertions/deletions have no paired formatting.
            foreach (var span in Split(change.DeleteStartA, change.InsertStartB, Math.Min(change.DeleteCountA, change.InsertCountB))) yield return span;
            left = change.DeleteStartA + change.DeleteCountA;
            right = change.InsertStartB + change.InsertCountB;
        }
        foreach (var span in Split(left, right, reference.Text.Length - left)) yield return span;

        IEnumerable<AlignedFormattingSpan> Split(int a, int b, int length)
        {
            var end = a + length;
            var ai = leftRuns.FindIndex(r => r.End > a);
            var bi = rightRuns.FindIndex(r => r.End > b);
            while (a < end && ai >= 0 && bi >= 0 && ai < leftRuns.Count && bi < rightRuns.Count)
            {
                var ar = leftRuns[ai]; var br = rightRuns[bi];
                var count = Math.Min(end - a, Math.Min(ar.End - a, br.End - b));
                yield return new(ar.Run, br.Run, a, b, count);
                a += count; b += count;
                if (a == ar.End) ai++;
                if (b == br.End) bi++;
            }
        }
    }

    private static List<(TextRun Run, int End)> Runs(ParagraphBlock paragraph)
    {
        var end = 0;
        return paragraph.Runs.Where(run => run.Text.Length > 0).Select(run => (run, end += run.Text.Length)).ToList();
    }
}
