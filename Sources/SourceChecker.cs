using System.Text.RegularExpressions;
using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.Sources;

/// <summary>
/// Checks each fact of a question-and-answer document against the page text of a source folder, on this computer.
/// For a fact with a label (a table row, or "retest period of 36 months"): the pages that print the label are searched,
/// pages of the section the answer belongs to first; next to the label (the rest of its line, or the next line) the
/// values must be the same → Matches, otherwise → Different in source. Without the label, values found close together
/// are shown to check (identifiers alone count as found). Sentences without values get their best passage to check.
/// </summary>
public sealed partial class SourceChecker
{
    // How far after a label its value may be printed, and how close values found without a label must be.
    private const int WindowLength = 260;
    private const int NearbyLength = 400;
    private readonly List<IndexedPage> _pages = [];
    private readonly Dictionary<string, HashSet<int>> _postings = new(StringComparer.Ordinal);

    public SourceChecker(SourceLibrary library)
    {
        foreach (var file in library.Files.Where(file => file.IsReadable))
        {
            if (library.TextOf(file) is not { } text)
                continue;
            foreach (var page in text.Pages)
            {
                var normalized = SourceTokens.Normalize(page.Text);
                var index = _pages.Count;
                _pages.Add(new IndexedPage(file, page.Number, page.Text, normalized, page.Ocr, Key(file.RelativePath),
                    SourceTokens.ValueTokens(page.Text)));
                foreach (var word in SourceTokens.Words(page.Text).Distinct())
                    Post(word, index);
                foreach (var token in _pages[index].Tokens)
                    Post(token.Key, index);
            }
        }
    }

    /// <summary>The pages that can be searched (pages of readable PDFs).</summary>
    public int PageCount => _pages.Count;

    /// <summary>Reads the document, finds its questions and facts, and checks every fact.</summary>
    public static SourceCheckResult Check(string documentPath, SourceLibrary library, IProgress<SourceCheckProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var read = new DocumentReader().Read(documentPath);
        if (read.Document is not { } document)
            return new SourceCheckResult(documentPath, library.Name, [], [], DateTimeOffset.Now,
                read.Issues.FirstOrDefault()?.Message ?? "The document could not be read.");
        return new SourceChecker(library).Check(document, documentPath, library.Name, progress, cancellation);
    }

    public SourceCheckResult Check(DocumentModel document, string documentPath, string libraryName, IProgress<SourceCheckProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var questions = QuestionAnswerReader.Read(document);
        if (questions.Count == 0)
            return new SourceCheckResult(documentPath, libraryName, [], [], DateTimeOffset.Now,
                "No questions were found in the document. Source check looks for questions ending in \"?\" followed by their answers.");
        if (_pages.Count == 0)
            return new SourceCheckResult(documentPath, libraryName, questions, [], DateTimeOffset.Now,
                "The source folder has no readable PDF pages. Upload your source PDFs first.");
        var results = new List<SourceFactResult>();
        for (var index = 0; index < questions.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var question = questions[index];
            progress?.Report(new SourceCheckProgress(index, questions.Count, question.Text));
            foreach (var fact in question.Facts)
                results.Add(Check(fact, question.Section));
        }
        progress?.Report(new SourceCheckProgress(questions.Count, questions.Count, ""));
        return new SourceCheckResult(documentPath, libraryName, questions, results, DateTimeOffset.Now);
    }

    /// <summary>Checks one fact. <paramref name="section"/> is the document section ("2.3.S.4"); its source section is searched first.</summary>
    public SourceFactResult Check(SourceFact fact, string section)
    {
        var hints = Hints(fact, section);
        if (fact.Kind == SourceFactKind.Statement)
            return CheckStatement(fact, hints);

        var tokens = SourceTokens.ValueTokens(fact.Value);
        var words = tokens.Count == 0 ? SourceTokens.SignificantWords(fact.Value) : [];
        if (tokens.Count == 0 && words.Count == 0)
            return new SourceFactResult(fact, SourceVerdict.CheckYourself, "This row has no value that can be compared.");

        // 1. Next to the label. When no place has the same value, the most likely place for the fact is reported:
        // a value on the label's own line, sharing some of the fact's values (for example the method AM-102).
        (IndexedPage Page, int LabelStart, int WindowEnd, string Window, int Score)? different = null;
        foreach (var pattern in LabelPatterns(fact.Label))
        {
            foreach (var pageIndex in Ordered(PagesWithAll(SourceTokens.Words(pattern.Words)), hints))
            {
                var page = _pages[pageIndex];
                foreach (Match label in pattern.Regex.Matches(page.Normalized))
                {
                    var (windowEnd, window) = Window(page, label.Index + label.Length, tokens.Count > 0);
                    var labelEnd = label.Index + label.Length;
                    if (Satisfies(page, labelEnd, windowEnd, tokens, words, out var lastEnd, out var conflict) && !conflict)
                        return new SourceFactResult(fact, SourceVerdict.Matches,
                            $"The same value is printed next to \"{fact.Label}\" in the source.",
                            Evidence(page, label.Index, lastEnd), window);
                    if (HasValues(page, labelEnd, windowEnd, tokens.Count > 0))
                    {
                        var score = Shared(page, labelEnd, windowEnd, tokens, words) * 10 +
                                    (page.Normalized.IndexOf('\n', labelEnd) is var lineEnd && (lineEnd < 0 || lineEnd >= windowEnd) ? 5 : 0);
                        if (different is null || score > different.Value.Score)
                            different = (page, label.Index, windowEnd, window, score);
                    }
                }
            }
            if (different is not null)
                break;   // a longer label was found: shorter forms of it would only find less exact places
        }
        if (different is { } place)
            return new SourceFactResult(fact, SourceVerdict.Different,
                $"The source has a different value next to \"{fact.Label}\": {place.Window}",
                Evidence(place.Page, place.LabelStart, place.WindowEnd), place.Window);

        // 2. The values without the label.
        if (tokens.Count > 0)
        {
            foreach (var pageIndex in Ordered(PagesWithAll(tokens.Select(token => token.Key)), hints))
            {
                var page = _pages[pageIndex];
                if (Nearby(page, tokens) is { } span)
                {
                    var identifiersOnly = tokens.All(token => !token.IsNumber);
                    return new SourceFactResult(fact, identifiersOnly ? SourceVerdict.Matches : SourceVerdict.CheckYourself,
                        identifiersOnly
                            ? "The identifier is printed in the source."
                            : fact.Label.Length > 0
                                ? $"The value is printed in the source, but not next to \"{fact.Label}\". Check that it is the same thing."
                                : "The values are printed together in the source. Check that they are about the same thing.",
                        Evidence(page, span.Start, span.End), Spaces().Replace(page.Text[span.Start..span.End], " ").Trim());
                }
            }
            return new SourceFactResult(fact, SourceVerdict.NotFound, fact.Label.Length > 0
                ? $"Neither \"{fact.Label}\" with this value nor the value itself was found in the source PDFs."
                : "These values were not found together in the source PDFs.");
        }

        foreach (var pageIndex in Ordered(PagesWithAll(words), hints))
        {
            var page = _pages[pageIndex];
            var first = words.Select(word => WholeWord(word).Match(page.Normalized)).Where(match => match.Success).MinBy(match => match.Index);
            if (first is not null)
                return new SourceFactResult(fact, SourceVerdict.CheckYourself,
                    $"\"{fact.Value}\" is printed in the source, but \"{fact.Label}\" was not found next to it. Check that it is the same thing.",
                    Evidence(page, first.Index, Math.Min(page.Text.Length, first.Index + fact.Value.Length)), Line(page, first.Index));
        }
        return new SourceFactResult(fact, SourceVerdict.NotFound, $"\"{fact.Value}\" was not found in the source PDFs.");
    }

    /// <summary>A sentence without values: the page sharing most of its words is shown to check.</summary>
    private SourceFactResult CheckStatement(SourceFact fact, IReadOnlyList<string> hints)
    {
        var words = SourceTokens.SignificantWords(fact.Value);
        var candidates = words.SelectMany(word => _postings.TryGetValue(word, out var pages) ? pages : []).Distinct();
        var best = Ordered(candidates, hints)
            .Select(index => (Index: index, Share: words.Count(word => _postings.TryGetValue(word, out var pages) && pages.Contains(index)) / (double)words.Count))
            .OrderByDescending(candidate => candidate.Share)
            .FirstOrDefault();
        if (best.Share < 0.5)
            return new SourceFactResult(fact, SourceVerdict.NotFound, "No passage of the source PDFs uses most of the words of this sentence.");
        var page = _pages[best.Index];
        var lines = LineSpans(page).OrderByDescending(line => words.Count(word => WholeWord(word).IsMatch(page.Normalized[line.Start..line.End]))).First();
        return new SourceFactResult(fact, SourceVerdict.CheckYourself,
            $"This sentence has no value to compare. The passage sharing most of its words ({best.Share:P0}) is shown: check that it says the same.",
            Evidence(page, lines.Start, lines.End), page.Text[lines.Start..lines.End].Trim());
    }

    // ---- matching ----

    /// <summary>
    /// Ways to find the label on a page, most exact first: all its words, then its last three and last two significant
    /// words ("Chemical Abstracts Service (CAS) Registry Number" → "cas registry number", "registry number").
    /// </summary>
    private static IEnumerable<(string Words, Regex Regex)> LabelPatterns(string label)
    {
        var all = SourceTokens.Words(label).ToList();
        var significant = all.Where(word => !SourceTokens.IsCommonWord(word)).ToList();
        var forms = new List<List<string>> { all };
        if (significant.Count > 3)
            forms.Add(significant.TakeLast(3).ToList());
        if (significant.Count > 2)
            forms.Add(significant.TakeLast(2).ToList());
        foreach (var form in forms.Where(form => form.Count > 0 && form.Sum(word => word.Length) >= 4).DistinctBy(form => string.Join(" ", form)))
            yield return (string.Join(" ", form),
                new Regex(@"(?<!\p{L})" + string.Join(@"[^\p{L}\p{N}]{1,6}", form.Select(Regex.Escape)) + @"(?!\p{L})", RegexOptions.CultureInvariant));
    }

    /// <summary>The text after a label where its value is printed: the rest of the line, and the next line when needed.</summary>
    private static (int End, string Text) Window(IndexedPage page, int start, bool numbers)
    {
        var text = page.Normalized;
        var lineEnd = text.IndexOf('\n', start);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        var end = lineEnd;
        var sameLine = text[start..lineEnd];
        var hasValue = numbers ? SourceTokens.ValueTokens(sameLine).Count > 0 : SourceTokens.SignificantWords(sameLine).Count > 0;
        if (!hasValue && lineEnd < text.Length)
        {
            var next = text.IndexOf('\n', lineEnd + 1);
            end = next < 0 ? text.Length : next;
        }
        end = Math.Min(end, start + WindowLength);
        return (end, Spaces().Replace(page.Text[start..end], " ").TrimStart(' ', ':', '|', '-', ')', ']').TrimEnd());
    }

    /// <summary>
    /// True when every value of the fact is printed between <paramref name="start"/> and <paramref name="end"/>.
    /// A conflict is the same number with another limit sign ("NLT 0.2%" for "NMT 0.2%").
    /// </summary>
    private static bool Satisfies(IndexedPage page, int start, int end, IReadOnlyList<ValueToken> tokens, IReadOnlyList<string> words,
        out int lastEnd, out bool conflict)
    {
        lastEnd = start;
        conflict = false;
        if (tokens.Count > 0)
        {
            var inside = page.Tokens.Where(token => token.Start >= start && token.Start + token.Length <= end).ToList();
            foreach (var token in tokens)
            {
                var found = inside.FirstOrDefault(candidate => candidate.Key == token.Key);
                if (found is null)
                    return false;
                if (token.Operator is not null && found.Operator is not null && token.Operator != found.Operator)
                    conflict = true;
                lastEnd = Math.Max(lastEnd, found.Start + found.Length);
            }
            return true;
        }
        var window = page.Normalized[start..end];
        foreach (var word in words)
        {
            var match = WholeWord(word).Match(window);
            if (!match.Success)
                return false;
            lastEnd = Math.Max(lastEnd, start + match.Index + match.Length);
        }
        return true;
    }

    /// <summary>How many of the fact's values are printed in the window.</summary>
    private static int Shared(IndexedPage page, int start, int end, IReadOnlyList<ValueToken> tokens, IReadOnlyList<string> words) => tokens.Count > 0
        ? tokens.Count(token => page.Tokens.Any(candidate => candidate.Key == token.Key && candidate.Start >= start && candidate.Start + candidate.Length <= end))
        : words.Count(word => WholeWord(word).IsMatch(page.Normalized[start..end]));

    private static bool HasValues(IndexedPage page, int start, int end, bool numbers) => numbers
        ? page.Tokens.Any(token => token.Start >= start && token.Start + token.Length <= end)
        : SourceTokens.SignificantWords(page.Normalized[start..end]).Count > 0;

    /// <summary>The shortest stretch of the page that holds every value, when it is short enough to be one passage.</summary>
    private static (int Start, int End)? Nearby(IndexedPage page, IReadOnlyList<ValueToken> tokens)
    {
        var keys = tokens.Select(token => token.Key).Distinct().ToList();
        var places = page.Tokens.Where(token => keys.Contains(token.Key)).OrderBy(token => token.Start).ToList();
        (int Start, int End)? best = null;
        for (var first = 0; first < places.Count; first++)
        {
            var seen = new HashSet<string>();
            for (var last = first; last < places.Count && places[last].Start - places[first].Start <= NearbyLength; last++)
            {
                seen.Add(places[last].Key);
                if (seen.Count == keys.Count)
                {
                    var span = (places[first].Start, places[last].Start + places[last].Length);
                    if (best is null || span.Item2 - span.Item1 < best.Value.End - best.Value.Start)
                        best = span;
                    break;
                }
            }
        }
        return best;
    }

    // ---- index ----

    private void Post(string key, int page)
    {
        if (!_postings.TryGetValue(key, out var pages))
            _postings[key] = pages = [];
        pages.Add(page);
    }

    private IEnumerable<int> PagesWithAll(IEnumerable<string> keys)
    {
        HashSet<int>? result = null;
        foreach (var key in keys.Distinct())
        {
            if (!_postings.TryGetValue(key, out var pages))
                return [];
            result = result is null ? [.. pages] : [.. result.Intersect(pages)];
        }
        return result ?? [];
    }

    /// <summary>Pages of the answer's own section first, then the others, each in folder order.</summary>
    private IEnumerable<int> Ordered(IEnumerable<int> pages, IReadOnlyList<string> hints) => pages
        .OrderByDescending(index => hints.Count(hint => _pages[index].PathKey.Contains(hint, StringComparison.Ordinal)))
        .ThenBy(index => _pages[index].File.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(index => _pages[index].Number);

    /// <summary>
    /// The source sections for a fact: sections it names ("3.2.S.4.1") and the eCTD body section of its QOS section
    /// (2.3.S.4 summarises 3.2.S.4), written as folder names are ("32s4").
    /// </summary>
    private static IReadOnlyList<string> Hints(SourceFact fact, string section)
    {
        var hints = SourceTokens.SectionHints(fact.Text).ToList();
        var normalized = section.ToLowerInvariant();
        if (normalized.StartsWith("2.3."))
            hints.Add(("3.2." + normalized[4..]).Replace(".", ""));
        else if (normalized.Length > 0)
            hints.Add(normalized.Replace(".", ""));
        return hints.Where(hint => hint.Length >= 3).Distinct().ToList();
    }

    private static SourceEvidence Evidence(IndexedPage page, int start, int end)
    {
        start = Math.Clamp(start, 0, page.Text.Length);
        end = Math.Clamp(end, start, page.Text.Length);
        return new SourceEvidence(page.File, page.Number, start, end - start, Spaces().Replace(page.Text[start..end], " ").Trim(), page.Ocr);
    }

    private static string Line(IndexedPage page, int at)
    {
        var start = page.Text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        var end = page.Text.IndexOf('\n', at);
        return page.Text[start..(end < 0 ? page.Text.Length : end)].Trim();
    }

    private static IEnumerable<(int Start, int End)> LineSpans(IndexedPage page)
    {
        var start = 0;
        while (start < page.Text.Length)
        {
            var end = page.Text.IndexOf('\n', start);
            end = end < 0 ? page.Text.Length : end;
            if (end > start)
                yield return (start, end);
            start = end + 1;
        }
    }

    private static string Key(string path) => new(path.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static Regex WholeWord(string word) => new(@"(?<!\p{L})" + Regex.Escape(word) + @"(?!\p{L})", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private sealed record IndexedPage(SourceFile File, int Number, string Text, string Normalized, bool Ocr, string PathKey, IReadOnlyList<ValueToken> Tokens);
}
