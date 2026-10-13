using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentChecker.Engine.Comparison;

namespace DocumentChecker.Engine.History;

/// <summary>One compared file as it was at the time of the comparison.</summary>
public sealed record HistoryFile(string Path, string Name, string? Sha256);

/// <summary>One difference as it was reported at the time of the comparison.</summary>
public sealed record HistoryFinding(
    string Id,
    string Change,
    string Reference,
    string Current,
    string ReferenceLocation,
    string CurrentLocation,
    bool CheckYourself);

/// <summary>A saved comparison: when, by whom, which files (with fingerprints), the result and every difference found.</summary>
public sealed record HistoryEntry(
    string Id,
    DateTimeOffset ComparedAt,
    string Reviewer,
    string Computer,
    HistoryFile Reference,
    HistoryFile Current,
    string Status,
    string Result,
    int Differences,
    int ToCheck,
    IReadOnlyDictionary<string, int> ByChange,
    IReadOnlyList<HistoryFinding> Findings,
    bool HeadersAndFootersCompared = true);

/// <summary>Whether a file still matches the fingerprint it had when it was compared.</summary>
public enum HistoryFileState { Unchanged, Changed, Missing, Unknown }

/// <summary>
/// The comparisons made on this computer, kept in a JSON Lines file in the user's application-data folder
/// (one comparison per line, appended, so an interrupted write can damage at most the last line, which is then skipped).
/// The file holds file names, paths and the differing values, so it stays on this computer and can be cleared.
/// </summary>
public sealed class ComparisonHistory
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    // JSON Lines files are plain UTF-8 without a byte-order mark, so other tools (Excel, Python, jq) read them too.
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();

    public ComparisonHistory(string? filePath = null)
    {
        // DOCHECK_HISTORY_FILE points tests and trial runs at their own file instead of the user's history.
        FilePath = filePath ?? Environment.GetEnvironmentVariable("DOCHECK_HISTORY_FILE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "DocumentChecker", "history.jsonl");
    }

    public string FilePath { get; }

    /// <summary>All saved comparisons, newest first. Damaged lines are skipped.</summary>
    public IReadOnlyList<HistoryEntry> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
                return [];
            var entries = new List<HistoryEntry>();
            foreach (var line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<HistoryEntry>(line, Json) is { } entry)
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A damaged line (for example an interrupted write) is skipped; the other entries remain.
                }
            }
            return entries.OrderByDescending(entry => entry.ComparedAt).ToList();
        }
    }

    /// <summary>
    /// Saves a completed comparison and returns its entry. Comparing the same two unchanged files again with the same
    /// result does not add a duplicate: the existing entry is returned instead.
    /// </summary>
    public HistoryEntry? Record(ComparisonResult result, string referencePath, string currentPath, DateTimeOffset? comparedAt = null)
    {
        if (!result.IsSuccess)
            return null;
        var entry = Create(result, referencePath, currentPath, comparedAt ?? DateTimeOffset.Now);
        lock (_gate)
        {
            var latest = Load().FirstOrDefault();
            if (latest is not null && latest.Reference.Sha256 == entry.Reference.Sha256 && latest.Current.Sha256 == entry.Current.Sha256 &&
                latest.Reference.Path == entry.Reference.Path && latest.Current.Path == entry.Current.Path &&
                latest.Result == entry.Result && latest.HeadersAndFootersCompared == entry.HeadersAndFootersCompared)
                return latest;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (StartsWithByteOrderMark())
                Rewrite(Load().OrderBy(saved => saved.ComparedAt));
            File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine, Utf8);
            return entry;
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var remaining = Load().Where(entry => entry.Id != id).OrderBy(entry => entry.ComparedAt).ToList();
            Rewrite(remaining);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
    }

    private void Rewrite(IEnumerable<HistoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllLines(temporary, entries.Select(entry => JsonSerializer.Serialize(entry, Json)), Utf8);
        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>Files written by earlier versions start with a UTF-8 byte-order mark; they are rewritten without it.</summary>
    private bool StartsWithByteOrderMark()
    {
        if (!File.Exists(FilePath))
            return false;
        using var stream = File.OpenRead(FilePath);
        Span<byte> start = stackalloc byte[3];
        return stream.Read(start) == 3 && start[0] == 0xEF && start[1] == 0xBB && start[2] == 0xBF;
    }

    /// <summary>The history entry for a comparison, without saving it.</summary>
    public static HistoryEntry Create(ComparisonResult result, string referencePath, string currentPath, DateTimeOffset comparedAt)
    {
        var toCheck = result.Differences.Count(NeedsChecking);
        var resultText = result.Status switch
        {
            ComparisonStatus.ExactFileMatch => "Exact File Match",
            ComparisonStatus.DocumentDataMatch => "No differences found",
            _ => $"{result.Differences.Count} difference{(result.Differences.Count == 1 ? "" : "s")}" +
                 (toCheck == 0 ? string.Empty : $" · {toCheck} to check yourself")
        };
        return new HistoryEntry(
            Guid.NewGuid().ToString("N"),
            comparedAt,
            Environment.UserName,
            Environment.MachineName,
            new HistoryFile(Path.GetFullPath(referencePath), Path.GetFileName(referencePath), result.ReferenceHash),
            new HistoryFile(Path.GetFullPath(currentPath), Path.GetFileName(currentPath), result.CurrentHash),
            result.Status.ToString(),
            resultText,
            result.Differences.Count,
            toCheck,
            result.Differences.GroupBy(FindingText.Operation).OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
                .ToDictionary(group => group.Key, group => group.Count()),
            result.Differences.Select(difference => new HistoryFinding(
                difference.Id,
                FindingText.Operation(difference),
                FindingText.PrintableValue(difference.ReferenceValue),
                FindingText.PrintableValue(difference.CurrentValue),
                FindingText.Location(result.ReferenceDocument, difference.ReferenceLocation, difference.ReferenceImageId is not null),
                FindingText.Location(result.CurrentDocument, difference.CurrentLocation, difference.CurrentImageId is not null),
                NeedsChecking(difference))).ToList(),
            result.UsedOptions.CompareHeadersAndFooters);
    }

    private static bool NeedsChecking(DocumentDifference difference) =>
        difference.Category == DifferenceCategory.Review || difference.IsRecognizedText || !string.IsNullOrWhiteSpace(difference.ReviewNote);

    /// <summary>Whether the file at the recorded path is still the file that was compared.</summary>
    public static HistoryFileState StateOf(HistoryFile file)
    {
        if (!File.Exists(file.Path))
            return HistoryFileState.Missing;
        if (file.Sha256 is null)
            return HistoryFileState.Unknown;
        try
        {
            using var stream = File.OpenRead(file.Path);
            return Convert.ToHexString(SHA256.HashData(stream)) == file.Sha256 ? HistoryFileState.Unchanged : HistoryFileState.Changed;
        }
        catch (IOException)
        {
            return HistoryFileState.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return HistoryFileState.Unknown;
        }
    }

    /// <summary>All entries as a CSV table, one row per comparison.</summary>
    public static string ToCsv(IEnumerable<HistoryEntry> entries)
    {
        var csv = new StringBuilder();
        AppendRow(csv, "Compared at", "Reviewer", "Computer", "Reference file", "Reference SHA-256", "Current file", "Current SHA-256",
            "Result", "Differences", "To check yourself", "Headers and footers compared", "Reference path", "Current path");
        foreach (var entry in entries)
            AppendRow(csv, entry.ComparedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), entry.Reviewer, entry.Computer,
                entry.Reference.Name, entry.Reference.Sha256 ?? "", entry.Current.Name, entry.Current.Sha256 ?? "", entry.Result,
                entry.Differences.ToString(CultureInfo.InvariantCulture), entry.ToCheck.ToString(CultureInfo.InvariantCulture),
                entry.HeadersAndFootersCompared ? "Yes" : "No", entry.Reference.Path, entry.Current.Path);
        return csv.ToString();
    }

    /// <summary>The differences of one comparison as a CSV table.</summary>
    public static string FindingsToCsv(HistoryEntry entry)
    {
        var csv = new StringBuilder();
        AppendRow(csv, "Compared at", entry.ComparedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            "Reference", entry.Reference.Name, "Current", entry.Current.Name,
            "Headers and footers compared", entry.HeadersAndFootersCompared ? "Yes" : "No");
        AppendRow(csv, "ID", "Change", "Reference value", "Current value", "Reference location", "Current location", "Check yourself");
        foreach (var finding in entry.Findings)
            AppendRow(csv, finding.Id, finding.Change, finding.Reference, finding.Current, finding.ReferenceLocation, finding.CurrentLocation,
                finding.CheckYourself ? "Yes" : "No");
        return csv.ToString();
    }

    private static void AppendRow(StringBuilder csv, params string[] cells) =>
        csv.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");

    /// <summary>
    /// A CSV cell. Quotes, commas and line breaks are quoted; a value that a spreadsheet would run as a formula
    /// (starting with = + - @) is prefixed with an apostrophe so it is shown as text.
    /// </summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
