namespace DocumentChecker.Engine.Comparison;

public static class TableAlignment
{
    // Exact anchors prevent a deleted middle row/column from shifting every following value.
    public static IReadOnlyList<(T? Reference, T? Current)> Align<T>(IReadOnlyList<T> reference,
        IReadOnlyList<T> current, Func<T, string> key) where T : class
    {
        var lengths = new int[reference.Count + 1, current.Count + 1];
        for (var i = reference.Count - 1; i >= 0; i--)
        for (var j = current.Count - 1; j >= 0; j--)
            lengths[i, j] = key(reference[i]) == key(current[j])
                ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var anchors = new List<(int Left, int Right)>();
        int left = 0, right = 0;
        while (left < reference.Count && right < current.Count)
        {
            if (key(reference[left]) == key(current[right])) anchors.Add((left++, right++));
            else if (lengths[left + 1, right] >= lengths[left, right + 1]) left++;
            else right++;
        }
        anchors.Add((reference.Count, current.Count));
        var result = new List<(T?, T?)>();
        left = right = 0;
        foreach (var anchor in anchors)
        {
            while (left < anchor.Left || right < anchor.Right)
                result.Add((left < anchor.Left ? reference[left++] : null, right < anchor.Right ? current[right++] : null));
            if (left < reference.Count && right < current.Count) result.Add((reference[left++], current[right++]));
        }
        return result;
    }
}
