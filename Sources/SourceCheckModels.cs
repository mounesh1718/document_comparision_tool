using System.Text;

namespace DocumentChecker.Engine.Sources;

/// <summary>A question of the document to check, with the section it is in and the facts found in its answer.</summary>
public sealed record SourceQuestion(int Number, string Section, string Text, IReadOnlyList<SourceFact> Facts);

public enum SourceFactKind
{
    /// <summary>A row of an answer table: the label in the first cell, the value in the others.</summary>
    TableRow,
    /// <summary>A part of a sentence with a value (number, identifier) in it.</summary>
    Value,
    /// <summary>A sentence without values: its passage can only be shown, not verified.</summary>
    Statement
}

/// <summary>
/// One checkable fact of an answer. <see cref="Label"/> is what the value is about ("Molecular Weight"); it may be
/// empty when the sentence has none. <see cref="Text"/> is the whole row or sentence as written.
/// </summary>
public sealed record SourceFact(int Id, int QuestionNumber, SourceFactKind Kind, string Label, string Value, string Text, int ParagraphIndex);

public enum SourceVerdict { Different, NotFound, CheckYourself, Matches }

/// <summary>Where a fact was found: a page of a source PDF and the characters of the page text to highlight.</summary>
public sealed record SourceEvidence(SourceFile File, int Page, int Start, int Length, string Snippet, bool Ocr);

/// <summary>The result for one fact, with the evidence and, when it differs, the value printed in the source.</summary>
public sealed record SourceFactResult(SourceFact Fact, SourceVerdict Verdict, string Explanation, SourceEvidence? Evidence = null, string? SourceValue = null);

/// <summary>The result of checking a document against a source folder.</summary>
public sealed record SourceCheckResult(
    string DocumentPath,
    string LibraryName,
    IReadOnlyList<SourceQuestion> Questions,
    IReadOnlyList<SourceFactResult> Results,
    DateTimeOffset CheckedAt,
    string? Problem = null)
{
    public int Count(SourceVerdict verdict) => Results.Count(result => result.Verdict == verdict);

    /// <summary>The results as CSV for Excel: one row per fact with its result and evidence.</summary>
    public string ToCsv()
    {
        var csv = new StringBuilder();
        Row("Question", "Section", "Question text", "Label", "Value in the document", "Result", "Value in the source", "Source file", "Page", "Explanation");
        foreach (var result in Results)
        {
            var question = Questions.FirstOrDefault(item => item.Number == result.Fact.QuestionNumber);
            Row(result.Fact.QuestionNumber.ToString(), question?.Section ?? "", question?.Text ?? "", result.Fact.Label, result.Fact.Value,
                VerdictText(result.Verdict), result.SourceValue ?? "", result.Evidence?.File.RelativePath ?? "",
                result.Evidence?.Page.ToString() ?? "", result.Explanation);
        }
        return csv.ToString();

        void Row(params string[] cells) => csv.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");
    }

    public static string VerdictText(SourceVerdict verdict) => verdict switch
    {
        SourceVerdict.Matches => "Matches source",
        SourceVerdict.Different => "Different in source",
        SourceVerdict.NotFound => "Not found in sources",
        _ => "Check yourself"
    };

    /// <summary>A CSV cell, quoted when needed; a value a spreadsheet would run as a formula is shown as text.</summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

/// <summary>Progress of a check: questions done out of all questions.</summary>
public sealed record SourceCheckProgress(int Done, int Total, string Current);
