using System.Text.RegularExpressions;
using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.Sources;

/// <summary>
/// Finds the questions of a question-and-answer document (for example a question-based Quality Overall Summary) and
/// splits each answer into facts that can be checked against the sources.
/// A question is a paragraph that ends with "?" (also inside a one-cell box). Its answer is everything after it up to the
/// next question or section heading. Section headings ("2.3.S.4 Control of Drug Substance") give the section of the
/// questions under them. Answer tables give one fact per row (label in the first cell); answer paragraphs give one
/// fact per sentence part that holds a value, and sentences without values are kept as statements.
/// </summary>
public static partial class QuestionAnswerReader
{
    public static IReadOnlyList<SourceQuestion> Read(DocumentModel document)
    {
        var questions = new List<SourceQuestion>();
        var section = "";
        string? question = null;
        var answer = new List<Item>();
        var factId = 0;

        void Finish()
        {
            if (question is null)
                return;
            var number = questions.Count + 1;
            questions.Add(new SourceQuestion(number, section, question, Facts(answer, number, ref factId)));
            question = null;
            answer.Clear();
        }

        foreach (var item in Items(document))
        {
            if (item.Row is null && IsHeading(item.Text, out var heading))
            {
                Finish();
                section = heading;
            }
            else if (item.Row is null && IsQuestion(item.Text))
            {
                Finish();
                question = QuestionNumber().Replace(item.Text.Trim(), "");
            }
            else if (question is not null)
                answer.Add(item);
        }
        Finish();
        return questions;
    }

    /// <summary>A paragraph of the body, or a row of a table with two or more filled cells.</summary>
    private sealed record Item(string Text, int ParagraphIndex, IReadOnlyList<string>? Row = null, bool IsHeaderRow = false);

    private static IEnumerable<Item> Items(DocumentModel document)
    {
        foreach (var block in document.DisplayBlocks)
        {
            switch (block)
            {
                case ParagraphDisplayBlock { Paragraph: var paragraph }:
                    if (TextOf(paragraph) is { Length: > 0 } text)
                        yield return new Item(text, paragraph.Index);
                    break;
                case TableDisplayBlock table:
                    var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(row => row.Cells.Count);
                    for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                    {
                        var cells = table.Rows[rowIndex].Cells.Where(cell => !cell.MergedFromAbove).ToList();
                        var texts = cells.Select(cell => string.Join(" ", cell.Paragraphs.Select(TextOf).Where(text => text.Length > 0))).ToList();
                        var filled = texts.Where(text => text.Length > 0).ToList();
                        var first = cells.SelectMany(cell => cell.Paragraphs).FirstOrDefault();
                        if (filled.Count >= 2)
                        {
                            // A header row ("Test | Acceptance criteria | Method") holds no facts.
                            var header = cells.Where(cell => cell.Paragraphs.Any(paragraph => TextOf(paragraph).Length > 0))
                                             .All(cell => cell.Paragraphs.SelectMany(paragraph => paragraph.Runs).Where(run => run.Text.Trim().Length > 0)
                                                 .All(run => run.Formatting.Bold == true))
                                         || (rowIndex == 0 && columns >= 3 && !filled.Any(text => text.Any(char.IsDigit)));
                            yield return new Item(string.Join(" | ", filled), first?.Index ?? -1, filled, header);
                        }
                        else
                        {
                            // A box with one cell (often the question itself) is read as its paragraphs.
                            foreach (var paragraph in cells.SelectMany(cell => cell.Paragraphs))
                                if (TextOf(paragraph) is { Length: > 0 } cellText)
                                    yield return new Item(cellText, paragraph.Index);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>The text as printed in the body: hidden text and superscript note markers ("acid*", "mercapto-³") left out.</summary>
    private static string TextOf(ParagraphBlock paragraph) => Spaces().Replace(
        string.Concat(paragraph.Runs.Where(run => run.Formatting.Hidden != true && run.Formatting.Superscript != true).Select(run => run.Text)), " ").Trim();

    private static bool IsQuestion(string text)
    {
        text = text.Trim();
        return text.Length is > 8 and < 600 && text.EndsWith('?');
    }

    private static bool IsHeading(string text, out string section)
    {
        var match = Heading().Match(text);
        section = match.Success ? match.Groups["number"].Value.TrimEnd('.') : "";
        return match.Success && text.Length < 160 && !text.TrimEnd().EndsWith('?');
    }

    private static List<SourceFact> Facts(List<Item> answer, int question, ref int id)
    {
        var facts = new List<SourceFact>();
        foreach (var item in answer)
        {
            if (item.Row is { } row)
            {
                if (item.IsHeaderRow)
                    continue;
                var value = string.Join(" | ", row.Skip(1));
                facts.Add(new SourceFact(++id, question, SourceFactKind.TableRow, row[0], value, item.Text, item.ParagraphIndex));
                continue;
            }
            foreach (var sentence in Sentences(item.Text))
            {
                var parts = Clauses(sentence).Where(part => SourceTokens.ValueTokens(part).Count > 0).ToList();
                if (parts.Count == 0)
                {
                    if (SourceTokens.Words(sentence).Count(word => !SourceTokens.IsCommonWord(word)) >= 5)
                        facts.Add(new SourceFact(++id, question, SourceFactKind.Statement, "", sentence, sentence, item.ParagraphIndex));
                    continue;
                }
                foreach (var part in parts)
                {
                    var (label, value) = SplitLabel(part);
                    facts.Add(new SourceFact(++id, question, SourceFactKind.Value, label, value, sentence, item.ParagraphIndex));
                }
            }
        }
        return facts;
    }

    /// <summary>The words just before the first value are its label ("a retest period of 36 months" → "retest period").</summary>
    private static (string Label, string Value) SplitLabel(string part)
    {
        var first = SourceTokens.ValueTokens(part).Min(token => token.Start);
        var before = SourceTokens.Words(part[..first]).Where(word => !SourceTokens.IsCommonWord(word)).TakeLast(3).ToList();
        return (string.Join(" ", before), part.Trim());
    }

    private static IEnumerable<string> Sentences(string text) =>
        SentenceEnd().Split(text).Select(sentence => sentence.Trim()).Where(sentence => sentence.Length > 0);

    private static IEnumerable<string> Clauses(string sentence) =>
        ClauseBreak().Split(sentence).Select(part => part.Trim()).Where(part => part.Length > 0);

    // "2.3.S.4", "3.2.P.5.1" or "2.3.1": numbers like "3.0" at the start of an answer sentence are not headings.
    [GeneratedRegex(@"^\s*(?<number>\d+(?:\.\d+)*\.[A-Za-z](?:\.\d+)*|\d+(?:\.\d+){2,5})\.?\s+\S")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\(?\d{1,3}[\.\)]\s+")]
    private static partial Regex QuestionNumber();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z(])")]
    private static partial Regex SentenceEnd();

    // "…25 °C/60% RH, a retest period of 36 months…" is two facts; lists ("PN-2201, PN-2202") and "1,000" stay together.
    [GeneratedRegex(@";\s+|,\s+(?=[a-z])")]
    private static partial Regex ClauseBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
