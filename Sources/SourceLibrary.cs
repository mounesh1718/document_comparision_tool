using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentChecker.Engine.Comparison;

namespace DocumentChecker.Engine.Sources;

/// <summary>A PDF in a source folder: where it sits in the folder, its fingerprint, and how its text was read.</summary>
public sealed record SourceFile(
    string RelativePath,
    long Size,
    string Sha256,
    DateTimeOffset UploadedAt,
    string? OriginalPath,
    int Pages = 0,
    int OcrPages = 0,
    int ScannedPagesNotRead = 0,
    string? Problem = null)
{
    [JsonIgnore]
    public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];

    /// <summary>The folders above the file inside the source folder ("" at the top), with / between them.</summary>
    [JsonIgnore]
    public string Folder => RelativePath.Contains('/') ? RelativePath[..RelativePath.LastIndexOf('/')] : "";

    /// <summary>The text of the file could be read (a damaged or password-protected PDF cannot).</summary>
    [JsonIgnore]
    public bool IsReadable => Problem is null;
}

/// <summary>A folder uploaded into a source folder, remembered so it can be brought up to date later.</summary>
public sealed record SourceUpload(string Name, string OriginalPath, DateTimeOffset UploadedAt);

/// <summary>Progress of an upload: files done out of all files, and the file being copied now.</summary>
public sealed record SourceProgress(int Done, int Total, string Current);

/// <summary>What an upload did. Every PDF copied was verified against the original's fingerprint.</summary>
public sealed record UploadReport(
    int PdfFiles,
    int Added,
    int Updated,
    int Unchanged,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<(string Path, string Problem)> Failed,
    IReadOnlyList<(string Path, string Problem)> Unreadable,
    bool Cancelled = false)
{
    /// <summary>PDFs that are in the source folder and identical to the original after this upload.</summary>
    public int Verified => Added + Updated + Unchanged;

    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                Cancelled
                    ? $"Upload stopped: {Verified} of {PdfFiles} PDF files uploaded and verified."
                    : $"{Verified} of {PdfFiles} PDF file{(PdfFiles == 1 ? "" : "s")} uploaded correctly ({Added} new, {Updated} updated, {Unchanged} unchanged)."
            };
            if (Failed.Count > 0)
                parts.Add($"{Failed.Count} could not be copied.");
            if (Unreadable.Count > 0)
                parts.Add($"{Unreadable.Count} cannot be read (for example password-protected).");
            if (Removed.Count > 0)
                parts.Add($"{Removed.Count} removed because they are no longer in your folder.");
            if (Skipped.Count > 0)
                parts.Add($"{Skipped.Count} file{(Skipped.Count == 1 ? " was" : "s were")} skipped (not PDF).");
            return string.Join(" ", parts);
        }
    }
}

/// <summary>
/// A source folder: copies of the reviewer's source PDFs, in the same folder structure, kept on this computer for
/// Source check. Every copy is verified against the original's SHA-256 fingerprint, and the text of every page is read
/// once at upload. The originals are only read, never changed.
/// Stored in DocumentChecker/Sources/&lt;name&gt;/: library.json (the list of files), files/ (the copies) and
/// text/ (the page text, by fingerprint).
/// </summary>
public sealed class SourceLibrary
{
    private const string ManifestName = "library.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private Manifest _manifest;

    private SourceLibrary(string folder, Manifest manifest)
    {
        Folder = folder;
        _manifest = manifest;
    }

    /// <summary>Where source folders are kept. DOCHECK_SOURCES_DIR points trial runs at their own folder.</summary>
    public static string DefaultRoot => Environment.GetEnvironmentVariable("DOCHECK_SOURCES_DIR") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "DocumentChecker", "Sources");

    public string Folder { get; }
    public string Name => _manifest.Name;
    public DateTimeOffset CreatedAt => _manifest.CreatedAt;
    public IReadOnlyList<SourceFile> Files => _manifest.Files;
    public IReadOnlyList<SourceUpload> Uploads => _manifest.Uploads;

    /// <summary>The folder that holds this source folder and the others.</summary>
    public string Root => Path.GetDirectoryName(Folder)!;

    /// <summary>The source folders on this computer (in <paramref name="root"/>, by default <see cref="DefaultRoot"/>), by name.</summary>
    public static IReadOnlyList<SourceLibrary> List(string? root = null)
    {
        root ??= DefaultRoot;
        if (!Directory.Exists(root))
            return [];
        return Directory.EnumerateDirectories(root)
            .Select(TryOpen)
            .OfType<SourceLibrary>()
            .OrderBy(library => library.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Creates an empty source folder. The name must be new; it is also the folder's name on disk.</summary>
    public static SourceLibrary Create(string name, string? root = null)
    {
        root ??= DefaultRoot;
        name = (name ?? "").Trim();
        if (name.Length == 0)
            throw new ArgumentException("Give the source folder a name.");
        if (name.Length > 80)
            throw new ArgumentException("The name is too long (80 characters at most).");
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).TrimEnd('.', ' ');
        if (safe.Length == 0 || safe.All(character => character == '_' || character == '.'))
            throw new ArgumentException("Use letters or numbers in the name.");
        if (List(root).Any(library => string.Equals(library.Name, name, StringComparison.CurrentCultureIgnoreCase)) ||
            Directory.Exists(Path.Combine(root, safe)))
            throw new ArgumentException($"A source folder named \"{name}\" already exists.");

        var folder = Path.Combine(root, safe);
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        Directory.CreateDirectory(Path.Combine(folder, "text"));
        var library = new SourceLibrary(folder, new Manifest(name, DateTimeOffset.Now, [], []));
        library.Save();
        return library;
    }

    /// <summary>Opens a source folder, or null when the folder is not one (or its list of files is damaged).</summary>
    public static SourceLibrary? TryOpen(string folder)
    {
        try
        {
            var path = Path.Combine(folder, ManifestName);
            if (!File.Exists(path))
                return null;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json);
            return manifest is null ? null : new SourceLibrary(folder, manifest with { Uploads = manifest.Uploads ?? [], Files = manifest.Files ?? [] });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Deletes the source folder and its copies from this computer. The originals are not touched.</summary>
    public void Delete()
    {
        foreach (var file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Folder, recursive: true);
    }

    /// <summary>The copy of a file on disk.</summary>
    public string PathOf(SourceFile file) => Path.Combine(Folder, "files", file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The page text of a file, as read at upload; null when it could not be read.</summary>
    public SourceText? TextOf(SourceFile file)
    {
        try
        {
            var path = TextPath(file.Sha256);
            return File.Exists(path) ? JsonSerializer.Deserialize<SourceText>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Uploads a folder with all its subfolders, keeping the same structure under the folder's own name. Uploading the
    /// same folder again brings the copy up to date: new and changed PDFs are copied, unchanged ones kept, and PDFs no
    /// longer in the folder are removed from the copy. Files that are not PDFs are skipped and listed.
    /// </summary>
    public UploadReport UploadFolder(string folder, IImageTextReader? imageTextReader = null, IProgress<SourceProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        folder = Path.GetFullPath(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"The folder {folder} was not found.");
        if (IsInside(folder, Root, orSame: true) || IsInside(Root, folder, orSame: true))
            throw new ArgumentException("Choose a folder outside the tool's own source folders.");
        var top = Path.GetFileName(folder);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };
        var all = Directory.EnumerateFiles(folder, "*", options)
            .Where(path => !Path.GetFileName(path).StartsWith('.') && !Path.GetFileName(path).StartsWith("~$"))
            .Select(path => (Original: path, Relative: top + "/" + Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var report = Upload(all, imageTextReader, progress, cancellation);

        // The copy mirrors the folder: PDFs that are no longer in it are removed (only after a complete upload).
        var removed = new List<string>();
        if (!report.Cancelled)
        {
            var present = all.Select(file => file.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _manifest.Files.Where(file => file.RelativePath.StartsWith(top + "/", StringComparison.OrdinalIgnoreCase) &&
                                                                 !present.Contains(file.RelativePath)).ToList())
            {
                RemoveCopy(stale);
                removed.Add(stale.RelativePath);
            }
        }
        _manifest.Uploads.RemoveAll(upload => string.Equals(upload.Name, top, StringComparison.OrdinalIgnoreCase));
        _manifest.Uploads.Add(new SourceUpload(top, folder, DateTimeOffset.Now));
        Save();
        return report with { Removed = removed };
    }

    /// <summary>Adds single PDFs at the top of the source folder. A PDF with the same name is replaced when it differs.</summary>
    public UploadReport AddFiles(IEnumerable<string> paths, IImageTextReader? imageTextReader = null, IProgress<SourceProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var files = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Original: path, Relative: Path.GetFileName(path))).ToList();
        var report = Upload(files, imageTextReader, progress, cancellation);
        Save();
        return report;
    }

    /// <summary>Brings every uploaded folder up to date from its original location (those that still exist).</summary>
    public IReadOnlyList<(SourceUpload Upload, UploadReport? Report)> UpdateFromOriginals(IImageTextReader? imageTextReader = null,
        IProgress<SourceProgress>? progress = null, CancellationToken cancellation = default)
    {
        var results = new List<(SourceUpload, UploadReport?)>();
        foreach (var upload in _manifest.Uploads.ToList())
        {
            if (cancellation.IsCancellationRequested)
                break;
            results.Add((upload, Directory.Exists(upload.OriginalPath)
                ? UploadFolder(upload.OriginalPath, imageTextReader, progress, cancellation)
                : null));
        }
        return results;
    }

    /// <summary>Removes one file's copy from the source folder.</summary>
    public void Remove(string relativePath)
    {
        if (_manifest.Files.FirstOrDefault(file => file.RelativePath == relativePath) is { } file)
        {
            RemoveCopy(file);
            Save();
        }
    }

    private UploadReport Upload(List<(string Original, string Relative)> files, IImageTextReader? imageTextReader,
        IProgress<SourceProgress>? progress, CancellationToken cancellation)
    {
        var reader = new SourceTextReader(imageTextReader ?? new SystemImageTextReader());
        var pdfs = files.Where(file => IsPdf(file.Original)).ToList();
        var skipped = files.Where(file => !IsPdf(file.Original)).Select(file => file.Relative).ToList();
        int added = 0, updated = 0, unchanged = 0;
        var failed = new List<(string, string)>();
        var unreadable = new List<(string, string)>();
        var cancelled = false;
        for (var index = 0; index < pdfs.Count; index++)
        {
            if (cancellation.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            var (original, relative) = pdfs[index];
            progress?.Report(new SourceProgress(index, pdfs.Count, relative));
            try
            {
                var existing = _manifest.Files.FirstOrDefault(file => string.Equals(file.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
                var hash = Fingerprint(original);
                SourceFile entry;
                if (existing is not null && existing.Sha256 == hash && File.Exists(PathOf(existing)) && Fingerprint(PathOf(existing)) == hash)
                {
                    unchanged++;
                    entry = File.Exists(TextPath(hash)) || !existing.IsReadable ? existing : ReadText(existing, reader);
                }
                else
                {
                    entry = Copy(original, relative, hash);
                    entry = ReadText(entry, reader);
                    if (existing is null)
                        added++;
                    else
                    {
                        updated++;
                        _manifest.Files.Remove(existing);
                        if (existing.RelativePath != relative)
                            TryDelete(PathOf(existing));
                        RemoveTextIfUnused(existing.Sha256);
                    }
                }
                _manifest.Files.RemoveAll(file => string.Equals(file.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
                _manifest.Files.Add(entry);
                if (entry.Problem is { } problem)
                    unreadable.Add((relative, problem));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failed.Add((relative, exception.Message));
            }
            // The list is saved as it goes, so a long upload that is interrupted keeps what was done.
            if (index % 20 == 19)
                Save();
        }
        progress?.Report(new SourceProgress(pdfs.Count, pdfs.Count, ""));
        _manifest.Files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return new UploadReport(pdfs.Count, added, updated, unchanged, [], skipped, failed, unreadable, cancelled);
    }

    /// <summary>Copies the original next to its place, checks the copy's fingerprint, and only then puts it in place.</summary>
    private SourceFile Copy(string original, string relative, string hash)
    {
        var destination = Path.Combine(Folder, "files", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".uploading";
        File.Copy(original, partial, overwrite: true);
        if (Fingerprint(partial) != hash)
        {
            TryDelete(partial);
            throw new IOException("The copy did not match the original (its fingerprint differs). Try again.");
        }
        if (File.Exists(destination))
            File.SetAttributes(destination, FileAttributes.Normal);
        File.Move(partial, destination, overwrite: true);
        return new SourceFile(relative, new FileInfo(destination).Length, hash, DateTimeOffset.Now, original);
    }

    /// <summary>Reads the page text of a copy once and keeps it by fingerprint; a PDF that cannot be read gets the reason.</summary>
    private SourceFile ReadText(SourceFile file, SourceTextReader reader)
    {
        try
        {
            var text = reader.Read(File.ReadAllBytes(PathOf(file)));
            var path = TextPath(file.Sha256);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteAtomically(path, JsonSerializer.Serialize(text, Json));
            return file with
            {
                Pages = text.Pages.Count,
                OcrPages = text.Pages.Count(page => page.Ocr),
                ScannedPagesNotRead = text.ScannedPagesNotRead,
                Problem = null
            };
        }
        catch (InvalidDataException exception)
        {
            return file with { Pages = 0, OcrPages = 0, ScannedPagesNotRead = 0, Problem = exception.Message };
        }
    }

    private void RemoveCopy(SourceFile file)
    {
        TryDelete(PathOf(file));
        _manifest.Files.Remove(file);
        RemoveTextIfUnused(file.Sha256);
        // Folders left empty are removed too, so the copy keeps the same structure as the original.
        for (var directory = Path.GetDirectoryName(PathOf(file)); directory is not null && IsInside(directory, Path.Combine(Folder, "files"));
             directory = Path.GetDirectoryName(directory))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                break;
            Directory.Delete(directory);
        }
    }

    private void RemoveTextIfUnused(string sha256)
    {
        if (_manifest.Files.All(file => file.Sha256 != sha256))
            TryDelete(TextPath(sha256));
    }

    private string TextPath(string sha256) => Path.Combine(Folder, "text", sha256 + ".json");

    private void Save() => WriteAtomically(Path.Combine(Folder, ManifestName), JsonSerializer.Serialize(_manifest, Json));

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".saving";
        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    internal static string Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string folder, bool orSame = false)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        if (relative == ".")
            return orSame;
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Manifest(string Name, DateTimeOffset CreatedAt, List<SourceUpload> Uploads, List<SourceFile> Files);
}
