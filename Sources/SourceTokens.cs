using System.Globalization;
using System.Text.RegularExpressions;

namespace DocumentChecker.Engine.Sources;

/// <summary>
/// A value in a text: a number ("0.20" is kept as "0.2") with its limit sign ("NMT" and "not more than" are "≤"), or an
/// identifier (CAS number "52-67-5", method "AM-101", batch "PN-2201", formula "C5H11NO2S"), with its position.
/// </summary>
public sealed record ValueToken(string Key, bool IsNumber, string? Operator, int Start, int Length);

/// <summary>
/// The rules that make the document and the sources comparable: the same letters and dashes, numbers by value, limit
/// words by meaning, and identifiers exactly. <see cref="Normalize"/> keeps every character in place, so a position in
/// normalized text is the same position in the original text.
/// </summary>
public static partial class SourceTokens
{
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "or", "in", "on", "at", "for", "to", "by", "with", "is", "are", "was", "were", "be", "been",
        "as", "from", "this", "that", "these", "those", "it", "its", "which", "all", "per", "not", "than", "based", "using",
        "approx", "approximately", "about", "each", "any", "has", "have", "had", "also", "when", "will", "shall", "may", "can",
        "into", "under", "over", "there", "their", "such", "both", "within", "between", "after", "before", "during", "used",
        "provided", "given", "see", "section", "table", "refer", "following", "following", "found", "shown", "proposed", "is"
    };

    /// <summary>Lower case, one kind of dash and plain spaces; every character stays at its position.</summary>
    public static string Normalize(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            span[index] = character switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '−' or '﹣' or '－' => '-',
                ' ' or ' ' or ' ' or '\t' => ' ',
                '\r' => '\n',
                _ => char.ToLowerInvariant(character)
            };
        }
    });

    public static bool IsCommonWord(string word) => Common.Contains(word.ToLowerInvariant());

    /// <summary>The words (letters only, two or more) in lower case.</summary>
    public static IReadOnlyList<string> Words(string text) => WordPattern().Matches(Normalize(text)).Select(match => match.Value).ToList();

    /// <summary>The words that say what something is about: no common words, no single letters.</summary>
    public static IReadOnlyList<string> SignificantWords(string text) => Words(text).Where(word => !IsCommonWord(word)).Distinct().ToList();

    /// <summary>The numbers and identifiers of a text, in order. Section numbers ("3.2.S.4.1") are not values.</summary>
    public static IReadOnlyList<ValueToken> ValueTokens(string text)
    {
        var normalized = Normalize(text);
        var taken = new bool[normalized.Length];
        var tokens = new List<ValueToken>();
        foreach (Match reference in SectionPattern().Matches(normalized))
            Take(reference.Index, reference.Length);
        foreach (Match code in CodePattern().Matches(normalized))
        {
            if (IsTaken(code.Index, code.Length))
                continue;
            Take(code.Index, code.Length);
            tokens.Add(new ValueToken(code.Value, false, null, code.Index, code.Length));
        }
        foreach (Match number in NumberPattern().Matches(normalized))
        {
            if (IsTaken(number.Index, number.Length))
                continue;
            tokens.Add(new ValueToken(CanonicalNumber(number.Value), true, OperatorBefore(normalized, number.Index, number.Index + number.Length),
                number.Index, number.Length));
        }
        return tokens.OrderBy(token => token.Start).ToList();

        void Take(int start, int length)
        {
            for (var index = start; index < start + length; index++)
                taken[index] = true;
        }

        bool IsTaken(int start, int length)
        {
            for (var index = start; index < start + length; index++)
                if (taken[index])
                    return true;
            return false;
        }
    }

    /// <summary>The section a text refers to, written without dots for comparing with folder names ("3.2.S.4.1" → "32s41").</summary>
    public static IReadOnlyList<string> SectionHints(string text) =>
        SectionPattern().Matches(Normalize(text)).Select(match => match.Value.Replace(".", "")).Distinct().ToList();

    /// <summary>"0.20" → "0.2", "1,000" → "1000", "97.0" → "97".</summary>
    public static string CanonicalNumber(string number) =>
        decimal.TryParse(number.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.############################", CultureInfo.InvariantCulture)
            : number;

    /// <summary>The limit sign written just before a number: ≤ (NMT, not more than, &lt;=), ≥ (NLT, not less than, &gt;=), &lt; or &gt;.</summary>
    private static string? OperatorBefore(string text, int start, int end)
    {
        var before = text[Math.Max(0, start - 20)..start].TrimEnd();
        if (before.EndsWith("nmt") || before.EndsWith("not more than") || before.EndsWith('≤') || before.EndsWith("<=") || before.EndsWith("max.") || before.EndsWith("maximum"))
            return "≤";
        if (before.EndsWith("nlt") || before.EndsWith("not less than") || before.EndsWith('≥') || before.EndsWith(">=") || before.EndsWith("min.") || before.EndsWith("minimum"))
            return "≥";
        // "<731>" is a chapter number, not a limit.
        var closed = end < text.Length && text[end] == '>';
        if (before.EndsWith('<') && !closed)
            return "<";
        if (before.EndsWith('>'))
            return ">";
        return null;
    }

    [GeneratedRegex(@"\p{L}{2,}")]
    private static partial Regex WordPattern();

    // 3.2.s.4.1, 2.3.p, 2.3.1 (three or more parts)
    [GeneratedRegex(@"(?<![\p{L}\p{N}.])\d+(?:\.\d+)*\.[a-z](?:\.\d+)*(?![\p{L}\p{N}])|(?<![\p{L}\p{N}.])\d+(?:\.\d+){2,}(?![\p{N}])")]
    private static partial Regex SectionPattern();

    // CAS numbers (52-67-5), letters with digits (am-101, pn-2201, c5h11no2s, usp43)
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:\d{2,7}-\d{2}-\d|[a-z]+-?\d[a-z0-9]*(?:-[a-z0-9]+)*)(?![\p{L}\p{N}])")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}.,])(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?(?![\p{N}])")]
    private static partial Regex NumberPattern();
}
