using System.Security.Cryptography;
using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.Comparison;

public sealed class DocumentComparisonService
{
    private readonly DocumentReader _reader = new();
    private readonly IImageTextReader _imageTextReader;
    private int _differenceNumber;
    private List<ParagraphPair> _paragraphPairs = [];
    private List<TablePair> _tablePairs = [];
    private Dictionary<DocumentImage, string> _imageHashes = new(ReferenceEqualityComparer.Instance);
    private Func<ParagraphBlock, string> _referenceMatchKey = paragraph => paragraph.Text;
    private Func<ParagraphBlock, string> _currentMatchKey = paragraph => paragraph.Text;

    public DocumentComparisonService(IImageTextReader? imageTextReader = null)
    {
        _imageTextReader = imageTextReader ?? new WindowsImageTextReader();
    }

    public ComparisonResult Compare(string? referencePath, string? currentPath)
    {
        _differenceNumber = 0;
        _paragraphPairs = [];
        _tablePairs = [];
        var referenceRead = _reader.Read(referencePath);
        var currentRead = _reader.Read(currentPath);
        var issues = referenceRead.Issues
            .Select(issue => new DocumentReadIssue(issue.Code, $"Reference DOCX ({Path.GetFileName(referencePath)}): {issue.Message}"))
            .Concat(currentRead.Issues.Select(issue => new DocumentReadIssue(issue.Code, $"Current DOCX ({Path.GetFileName(currentPath)}): {issue.Message}")))
            .ToList();

        if (!referenceRead.IsSuccess || !currentRead.IsSuccess)
        {
            return new ComparisonResult(
                ComparisonStatus.InvalidInput,
                referenceRead.Document,
                currentRead.Document,
                [],
                issues,
                null,
                null);
        }

        var referenceHash = CalculateSha256(referencePath!);
        var currentHash = CalculateSha256(currentPath!);
        var reference = referenceRead.Document!;
        var current = currentRead.Document!;

        if (string.Equals(referenceHash, currentHash, StringComparison.Ordinal))
        {
            return new ComparisonResult(
                ComparisonStatus.ExactFileMatch,
                reference,
                current,
                [],
                [],
                referenceHash,
                currentHash);
        }

        var mixed = ImageTableComparer.Compare(reference, current, _imageTextReader);
        var referenceComparison = ExcludeMixed(reference, mixed.ReferenceImages, mixed.ReferenceTables, mixed.ReferenceParagraphs);
        var currentComparison = ExcludeMixed(current, mixed.CurrentImages, mixed.CurrentTables, mixed.CurrentParagraphs);
        _imageHashes = new Dictionary<DocumentImage, string>(ReferenceEqualityComparer.Instance);
        foreach (var image in reference.ImageItems.Concat(current.ImageItems))
            _imageHashes[image] = Convert.ToHexString(SHA256.HashData(image.Data));
        _referenceMatchKey = MatchKey(reference);
        _currentMatchKey = MatchKey(current);
        // Paragraphs first: tables and images are then judged against the paragraph pairing.
        var differences = CompareParagraphs(referenceComparison, currentComparison);
        differences.AddRange(CompareTables(referenceComparison, currentComparison));
        differences.AddRange(CompareImages(referenceComparison, currentComparison));
        differences.AddRange(mixed.Differences);
        foreach (var note in reference.ReviewNotes ?? [])
            differences.Add(CreateReviewDifference(reference, note, isReference: true));
        foreach (var note in current.ReviewNotes ?? [])
            differences.Add(CreateReviewDifference(current, note, isReference: false));
        differences = differences.Select(difference => difference with
        {
            ReferenceLocation = AddPage(difference.ReferenceLocation, difference.ReferenceRange, difference.ReferenceImageId, reference),
            CurrentLocation = AddPage(difference.CurrentLocation, difference.CurrentRange, difference.CurrentImageId, current),
            ReferenceContentKind = ContentKindFor(difference, isReference: true),
            CurrentContentKind = ContentKindFor(difference, isReference: false)
        }).ToList();
        var status = differences.Count == 0
            ? ComparisonStatus.DocumentDataMatch
            : ComparisonStatus.Different;

        return new ComparisonResult(status, reference, current, differences, [], referenceHash, currentHash, _paragraphPairs, _tablePairs);
    }

    private static DifferenceContentKind ContentKindFor(DocumentDifference difference, bool isReference)
    {
        // A review item sits on one side only; both sides describe that same content.
        if (difference.Category == DifferenceCategory.Review)
            return difference.ReferenceTableIndex is not null || difference.CurrentTableIndex is not null ||
                (difference.ReferenceLocation ?? difference.CurrentLocation)?.TableCell is not null
                ? DifferenceContentKind.Table : DifferenceContentKind.Text;

        var value = isReference ? difference.ReferenceValue : difference.CurrentValue;
        var location = isReference ? difference.ReferenceLocation : difference.CurrentLocation;
        var imageId = isReference ? difference.ReferenceImageId : difference.CurrentImageId;
        var tableIndex = isReference ? difference.ReferenceTableIndex : difference.CurrentTableIndex;
        if (value.StartsWith("(no corresponding", StringComparison.Ordinal))
            return DifferenceContentKind.Missing;
        if (imageId is not null || difference.Category == DifferenceCategory.Image && location is not null && tableIndex is null && location.TableCell is null)
            return DifferenceContentKind.Image;
        if (tableIndex is not null || location?.TableCell is not null || difference.Category == DifferenceCategory.Table)
            return DifferenceContentKind.Table;
        return DifferenceContentKind.Text;
    }

    private static DocumentModel ExcludeMixed(DocumentModel document, IReadOnlySet<string> images, IReadOnlySet<int> tables, IReadOnlySet<int> paragraphs) =>
        document with
        {
            Images = document.ImageItems.Where(image => !images.Contains(image.Id)).ToList(),
            Paragraphs = document.Paragraphs.Where(p =>
                !paragraphs.Contains(p.Index) &&
                !(p.TableLocation is { } cell && tables.Contains(cell.TableIndex)) &&
                !(p.Text.Length == 0 && p.Runs.SelectMany(run => run.ImageReferences).Any(images.Contains))).ToList(),
            Blocks = document.DisplayBlocks.Where(block => block is not TableDisplayBlock table || !tables.Contains(table.TableIndex)).ToList()
        };

    private static DocumentLocation? AddPage(DocumentLocation? location, DocumentRange? range, string? imageId, DocumentModel document)
    {
        if (location is null)
            return null;
        var paragraph = document.Paragraphs.FirstOrDefault(item => item.Index == location.ParagraphIndex);
        var offset = range?.Start ?? paragraph?.Runs.Take(location.RunIndex ?? 0).Sum(run => run.Text.Length) ?? 0;
        var page = imageId is null ? paragraph?.PageAt(offset) : document.ImageItems.FirstOrDefault(image => image.Id == imageId)?.Page;
        return location with { Page = page, TableCell = location.TableCell ?? paragraph?.TableLocation };
    }

    private List<DocumentDifference> CompareParagraphs(DocumentModel reference, DocumentModel current)
    {
        var differences = new List<DocumentDifference>();
        // Each header, footer and note is compared only with its counterpart; the body (null key) with the body.
        foreach (var partKey in PartKeys(reference, current))
            differences.AddRange(ComparePartParagraphs(
                reference.Paragraphs.Where(paragraph => !paragraph.IsInTable && paragraph.Part?.Key == partKey).ToList(),
                current.Paragraphs.Where(paragraph => !paragraph.IsInTable && paragraph.Part?.Key == partKey).ToList()));
        return differences;
    }

    /// <summary>Body first, then every part in document order, so findings read top to bottom.</summary>
    private static IReadOnlyList<string?> PartKeys(DocumentModel reference, DocumentModel current) =>
        Keys(reference).Concat(Keys(current)).Distinct().ToList();

    private static IEnumerable<string?> Keys(DocumentModel document) =>
        document.DisplayBlocks.Select(block => block is PartDisplayBlock part ? part.Part.Key : null);

    /// <summary>Tables everywhere in the document, including inside headers, footers and notes.</summary>
    private static IEnumerable<TableDisplayBlock> AllTables(DocumentModel document) => document.DisplayBlocks
        .SelectMany(block => block is PartDisplayBlock part ? part.Blocks : [block])
        .OfType<TableDisplayBlock>();

    /// <summary>
    /// What makes two paragraphs identical for alignment: their text and the pictures they hold. Without the
    /// pictures, every image-only paragraph (empty text) would match any empty paragraph.
    /// </summary>
    private Func<ParagraphBlock, string> MatchKey(DocumentModel document)
    {
        var hashes = document.ImageItems.ToDictionary(image => image.Id, image => _imageHashes[image]);
        return paragraph =>
        {
            var pictures = paragraph.Runs.SelectMany(run => run.ImageReferences).ToList();
            return pictures.Count == 0
                ? paragraph.Text
                : paragraph.Text + "\u0001" + string.Join(",", pictures.Select(id => hashes.GetValueOrDefault(id, id)));
        };
    }

    private List<DocumentDifference> ComparePartParagraphs(IReadOnlyList<ParagraphBlock> referenceParagraphs, IReadOnlyList<ParagraphBlock> currentParagraphs)
    {
        var differences = new List<DocumentDifference>();
        var pairs = AlignItems(referenceParagraphs, currentParagraphs, _referenceMatchKey, _currentMatchKey,
            (left, right) => Similarity(left.Text, right.Text));
        for (var index = 0; index < pairs.Count; index++)
        {
            var pair = pairs[index];
            if (pair.Reference is null)
            {
                // Point the Reference view at the paragraph this addition precedes.
                var next = pairs.Skip(index + 1).FirstOrDefault(item => item.Reference is not null)?.Reference;
                differences.Add(CreateParagraphDifference(pair.Current!, isAdded: true) with
                {
                    CounterpartParagraphIndex = next?.Index ?? DocumentDifference.EndOfDocument
                });
                continue;
            }

            if (pair.Current is null)
            {
                var next = pairs.Skip(index + 1).FirstOrDefault(item => item.Current is not null)?.Current;
                differences.Add(CreateParagraphDifference(pair.Reference, isAdded: false) with
                {
                    CounterpartParagraphIndex = next?.Index ?? DocumentDifference.EndOfDocument
                });
                continue;
            }

            _paragraphPairs.Add(new(pair.Reference.Index, pair.Current.Index));
            differences.AddRange(CreateTextDifferences(pair.Reference, pair.Current));
            differences.AddRange(CreateFormattingDifferences(pair.Reference, pair.Current));
            differences.AddRange(CreateParagraphFormattingDifferences(pair.Reference, pair.Current));
        }

        return differences;
    }

    private IEnumerable<DocumentDifference> CreateTextDifferences(ParagraphBlock reference, ParagraphBlock current)
    {
        if (string.Equals(reference.Text, current.Text, StringComparison.Ordinal))
            return [];

        return TextChangeAnalyzer.Find(reference.Text, current.Text).Select(block =>
        {
            var referenceValue = Slice(reference.Text, block.ReferenceStart, block.ReferenceLength);
            var currentValue = Slice(current.Text, block.CurrentStart, block.CurrentLength);
            return new DocumentDifference(
                NextId("TXT"),
                DifferenceCategory.Text,
                "Text",
                DisplayValue(referenceValue),
                DisplayValue(currentValue),
                BuildLocation(reference, block.ReferenceStart),
                BuildLocation(current, block.CurrentStart),
                new DocumentRange(reference.Index, block.ReferenceStart, block.ReferenceLength),
                new DocumentRange(current.Index, block.CurrentStart, block.CurrentLength),
                Nearby(reference.Text, current.Text));
        });
    }

    private IEnumerable<DocumentDifference> CreateFormattingDifferences(ParagraphBlock reference, ParagraphBlock current)
    {
        var differences = new List<DocumentDifference>();
        foreach (var span in FormattingAlignment.Align(reference, current))
        {
            var referenceRun = span.ReferenceRun;
            var currentRun = span.CurrentRun;

            foreach (var property in FormattingProperties(referenceRun.Formatting, currentRun.Formatting))
            {
                var before = DisplayFormattingValue(property.Name, property.Reference);
                var after = DisplayFormattingValue(property.Name, property.Current);
                if (before == after)
                    continue;

                differences.Add(new DocumentDifference(
                    NextId("FMT"),
                    DifferenceCategory.Formatting,
                    property.Name,
                    before,
                    after,
                    new DocumentLocation(reference.Index, referenceRun.Index),
                    new DocumentLocation(current.Index, currentRun.Index),
                    new DocumentRange(reference.Index, span.ReferenceStart, span.Length),
                    new DocumentRange(current.Index, span.CurrentStart, span.Length),
                    Nearby(reference.Text, current.Text)));
            }
        }

        return differences;
    }

    private IEnumerable<DocumentDifference> CreateParagraphFormattingDifferences(ParagraphBlock reference, ParagraphBlock current)
    {
        var differences = new List<DocumentDifference>();
        var referenceLayout = reference.Layout ?? new ParagraphLayout(null, null, null, null, null, null, false);
        var currentLayout = current.Layout ?? new ParagraphLayout(null, null, null, null, null, null, false);

        foreach (var property in ParagraphFormattingProperties(referenceLayout, currentLayout))
        {
            if (Equals(property.Reference, property.Current))
                continue;

            differences.Add(new DocumentDifference(
                NextId("PAR"),
                DifferenceCategory.Formatting,
                property.Name,
                DisplayParagraphFormattingValue(property.Reference),
                DisplayParagraphFormattingValue(property.Current),
                BuildLocation(reference, 0),
                BuildLocation(current, 0),
                new DocumentRange(reference.Index, 0, reference.Text.Length),
                new DocumentRange(current.Index, 0, current.Text.Length),
                Nearby(reference.Text, current.Text)));
        }

        return differences;
    }

    /// <summary>A finding for content that was not compared, placed where it occurs so the viewer can mark it.</summary>
    private DocumentDifference CreateReviewDifference(DocumentModel document, DocumentReviewItem note, bool isReference)
    {
        var table = note.TableIndex is { } tableIndex
            ? AllTables(document).FirstOrDefault(item => item.TableIndex == tableIndex)
            : null;
        var paragraph = note.ParagraphIndex is { } paragraphIndex
            ? document.Paragraphs.FirstOrDefault(item => item.Index == paragraphIndex)
            : FirstParagraph(table);
        var location = paragraph is null ? null : BuildLocation(paragraph, 0);
        var side = isReference ? "Reference" : "Current";
        var context = paragraph is null || string.IsNullOrWhiteSpace(paragraph.Text)
            ? $"{side}: {note.Message}"
            : $"{side}: {note.Message} Near: {Trim(paragraph.Text)}";
        return new DocumentDifference(NextId("REV"), DifferenceCategory.Review,
            table is not null ? "Table needs review" : paragraph is not null ? "Content needs review" : "Document part needs review",
            isReference ? "Not verified" : "(not checked here)", isReference ? "(not checked here)" : "Not verified",
            isReference ? location : null, isReference ? null : location, null, null, context,
            ReferenceTableIndex: isReference ? table?.TableIndex : null,
            CurrentTableIndex: isReference ? null : table?.TableIndex,
            ReviewNote: note.Message);
    }

    private DocumentDifference CreateParagraphDifference(ParagraphBlock paragraph, bool isAdded)
    {
        return new DocumentDifference(
            NextId(isAdded ? "ADD" : "DEL"),
            isAdded ? DifferenceCategory.ParagraphAdded : DifferenceCategory.ParagraphRemoved,
            "Paragraph",
            isAdded ? "(no corresponding content)" : paragraph.Text,
            isAdded ? paragraph.Text : "(no corresponding content)",
            isAdded ? null : BuildLocation(paragraph, 0),
            isAdded ? BuildLocation(paragraph, 0) : null,
            isAdded ? null : new DocumentRange(paragraph.Index, 0, paragraph.Text.Length),
            isAdded ? new DocumentRange(paragraph.Index, 0, paragraph.Text.Length) : null,
            paragraph.Text);
    }

    private IEnumerable<DocumentDifference> CompareImages(DocumentModel reference, DocumentModel current)
    {
        var differences = new List<DocumentDifference>();
        foreach (var partKey in PartKeys(reference, current))
            differences.AddRange(ComparePartImages(ImagesIn(reference, partKey), ImagesIn(current, partKey)));
        return differences;
    }

    private static List<DocumentImage> ImagesIn(DocumentModel document, string? partKey)
    {
        var partOf = document.Paragraphs.ToDictionary(paragraph => paragraph.Index, paragraph => paragraph.Part?.Key);
        return document.ImageItems.Where(image => partOf.GetValueOrDefault(image.ParagraphIndex) == partKey).ToList();
    }

    private List<DocumentDifference> ComparePartImages(IReadOnlyList<DocumentImage> referenceImages, IReadOnlyList<DocumentImage> currentImages)
    {
        var differences = new List<DocumentDifference>();
        var counterparts = _paragraphPairs.ToDictionary(pair => pair.ReferenceParagraphIndex, pair => pair.CurrentParagraphIndex);
        // Identical pictures anchor the pairing, so an inserted picture does not shift every later pair.
        // Between them, a picture whose paragraph is paired with the other picture's paragraph is preferred.
        var pairs = AlignItems(referenceImages, currentImages, image => _imageHashes[image], image => _imageHashes[image],
            (left, right) => counterparts.GetValueOrDefault(left.ParagraphIndex, -1) == right.ParagraphIndex ? 1 : 0.5);
        foreach (var (referenceImage, currentImage) in pairs)
        {
            var contentChanged = referenceImage is null || currentImage is null ||
                _imageHashes[referenceImage] != _imageHashes[currentImage];
            var sizeChanged = referenceImage is not null && currentImage is not null &&
                (referenceImage.WidthEmu != currentImage.WidthEmu || referenceImage.HeightEmu != currentImage.HeightEmu);
            // Paragraph numbers shift whenever content is added above, so a picture has only moved when its
            // paragraph is paired with a different paragraph than the one holding the other picture.
            var positionChanged = referenceImage is not null && currentImage is not null &&
                counterparts.TryGetValue(referenceImage.ParagraphIndex, out var expectedParagraph) &&
                expectedParagraph != currentImage.ParagraphIndex;

            var regions = referenceImage is not null && currentImage is not null && contentChanged
                ? ImageDifferenceAnalyzer.FindChangedRegions(referenceImage.Data, currentImage.Data)
                : [];
            if (regions.Count > 0)
            {
                var imageChanges = ImageContentComparer.Compare(_imageTextReader.Read(referenceImage!.Data),
                    _imageTextReader.Read(currentImage!.Data), regions);
                foreach (var change in imageChanges)
                    differences.Add(CreateImageDifference(referenceImage, currentImage,
                        change.IsText ? "Text in image" : "Visual image change", change.Context,
                        change.ReferenceRegion ?? change.CurrentRegion, change.Reference, change.Current) with
                    {
                        CurrentImageRegion = change.CurrentRegion ?? change.ReferenceRegion,
                        IsRecognizedText = change.IsText,
                        ReviewNote = change.ReviewNote
                    });
            }
            else if (contentChanged)
                differences.Add(CreateImageDifference(referenceImage, currentImage, "Image content",
                    "Image added, removed, resized, or encoded differently. Review the previews.", null,
                    referenceImage is null ? "(no corresponding image)" : "Reference image",
                    currentImage is null ? "(no corresponding image)" : "Current image"));
            if (sizeChanged)
            {
                if (referenceImage!.WidthEmu != currentImage!.WidthEmu)
                    differences.Add(CreateImageDifference(referenceImage, currentImage, "Width", "The displayed image width changed.", null,
                        DisplayEmu(referenceImage.WidthEmu), DisplayEmu(currentImage.WidthEmu)));
                if (referenceImage.HeightEmu != currentImage.HeightEmu)
                    differences.Add(CreateImageDifference(referenceImage, currentImage, "Height", "The displayed image height changed.", null,
                        DisplayEmu(referenceImage.HeightEmu), DisplayEmu(currentImage.HeightEmu)));
            }
            if (referenceImage is not null && currentImage is not null)
            {
                AddImagePropertyDifference(differences, referenceImage, currentImage, "Alt text", referenceImage.AltText, currentImage.AltText);
                AddImagePropertyDifference(differences, referenceImage, currentImage, "Caption", referenceImage.Caption, currentImage.Caption);
                AddImagePropertyDifference(differences, referenceImage, currentImage, "Alignment", referenceImage.Alignment, currentImage.Alignment);
            }
            if (positionChanged)
                differences.Add(CreateImageDifference(referenceImage, currentImage, "Image placement", "Image moved to a different paragraph.", null,
                    new DocumentLocation(referenceImage!.ParagraphIndex, referenceImage.RunIndex).Display,
                    new DocumentLocation(currentImage!.ParagraphIndex, currentImage.RunIndex).Display));
        }

        return differences;
    }

    private void AddImagePropertyDifference(
        ICollection<DocumentDifference> differences,
        DocumentImage referenceImage,
        DocumentImage currentImage,
        string property,
        string? referenceValue,
        string? currentValue)
    {
        if (!string.Equals(referenceValue, currentValue, StringComparison.Ordinal))
            differences.Add(CreateImageDifference(referenceImage, currentImage, property, $"Image {property.ToLowerInvariant()} changed.", null,
                DisplayImageProperty(referenceValue), DisplayImageProperty(currentValue)));
    }

    private DocumentDifference CreateImageDifference(
        DocumentImage? referenceImage,
        DocumentImage? currentImage,
        string property,
        string context,
        ImageDifferenceRegion? region,
        string? referenceValue = null,
        string? currentValue = null) =>
        new(NextId("IMG"), DifferenceCategory.Image, property, referenceValue ?? DescribeImage(referenceImage), currentValue ?? DescribeImage(currentImage),
            referenceImage is null ? null : new DocumentLocation(referenceImage.ParagraphIndex, referenceImage.RunIndex),
            currentImage is null ? null : new DocumentLocation(currentImage.ParagraphIndex, currentImage.RunIndex),
            null, null, context, referenceImage?.Id, currentImage?.Id, ImageRegion: region);

    private IEnumerable<DocumentDifference> CompareTables(DocumentModel reference, DocumentModel current)
    {
        var differences = new List<DocumentDifference>();
        foreach (var partKey in PartKeys(reference, current))
            differences.AddRange(ComparePartTables(
                AllTables(reference).Where(table => table.Part?.Key == partKey).ToList(),
                AllTables(current).Where(table => table.Part?.Key == partKey).ToList()));
        return differences;
    }

    private List<DocumentDifference> ComparePartTables(IReadOnlyList<TableDisplayBlock> referenceTables, IReadOnlyList<TableDisplayBlock> currentTables)
    {
        var differences = new List<DocumentDifference>();
        // Tables are paired by content, so a table inserted or removed in the middle does not shift every
        // later pair. Identical tables anchor the pairing; edited tables pair with their most similar match.
        foreach (var (referenceTable, currentTable) in AlignItems(referenceTables, currentTables, TableText, TableText,
                     (left, right) => Similarity(TableText(left), TableText(right))))
        {
            if (referenceTable is null || currentTable is null)
            {
                differences.Add(CreateTableDifference(
                    referenceTable,
                    currentTable,
                    "Table",
                    referenceTable is null ? "(no corresponding table)" : DescribeTable(referenceTable),
                    currentTable is null ? "(no corresponding table)" : DescribeTable(currentTable),
                    "A table exists on only one side of the comparison."));
                continue;
            }

            _tablePairs.Add(new(referenceTable.TableIndex, currentTable.TableIndex));
            differences.AddRange(CompareTablePair(referenceTable, currentTable));
        }

        return differences;
    }

    private IEnumerable<DocumentDifference> CompareTablePair(TableDisplayBlock reference, TableDisplayBlock current)
    {
        var differences = new List<DocumentDifference>();
        if (reference.Rows.Count != current.Rows.Count)
        {
            differences.Add(CreateTableDifference(
                reference,
                current,
                "Rows",
                reference.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                current.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Table row count changed."));
        }

        var referenceHeaders = reference.Rows.FirstOrDefault()?.Cells ?? [];
        var currentHeaders = current.Rows.FirstOrDefault()?.Cells ?? [];
        var columnPairs = TableAlignment.Align(referenceHeaders, currentHeaders, CellValue);
        var useHeaders = UniqueNonEmpty(referenceHeaders.Select(CellValue)) && UniqueNonEmpty(currentHeaders.Select(CellValue)) &&
            referenceHeaders.Select(CellValue).Intersect(currentHeaders.Select(CellValue)).Any();
        var useRowKeys = UniqueNonEmpty(reference.Rows.Select(RowKey)) && UniqueNonEmpty(current.Rows.Select(RowKey));
        foreach (var (referenceRow, currentRow) in TableAlignment.Align(reference.Rows, current.Rows,
                     row => useRowKeys ? RowKey(row) : string.Join("\u001f", row.Cells.Select(CellValue))))
        {
            if (referenceRow is null || currentRow is null)
            {
                foreach (var cell in (referenceRow ?? currentRow)!.Cells)
                foreach (var difference in CompareTableCell(referenceRow is null ? null : cell, currentRow is null ? null : cell))
                    differences.Add(difference with { Property = referenceRow is null ? "Added row value" : "Missing row value" });
                continue;
            }

            var rowIndex = reference.Rows.ToList().IndexOf(referenceRow);

            if (referenceRow.Cells.Count != currentRow.Cells.Count)
            {
                differences.Add(CreateTableDifference(
                    reference,
                    current,
                    $"Cells in row {rowIndex + 1}",
                    referenceRow.Cells.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    currentRow.Cells.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"Table {reference.TableIndex + 1}, row {rowIndex + 1} has a different cell count."));
            }

            if (useHeaders && referenceRow.Cells.Count == referenceHeaders.Count && currentRow.Cells.Count == currentHeaders.Count)
            {
                foreach (var (a, b) in columnPairs)
                {
                    var referenceCell = a is null ? null : referenceRow.Cells[referenceHeaders.ToList().IndexOf(a)];
                    var currentCell = b is null ? null : currentRow.Cells[currentHeaders.ToList().IndexOf(b)];
                    foreach (var difference in CompareTableCell(referenceCell, currentCell))
                        differences.Add(difference with { Property = a is null ? "Added column value" : b is null ? "Missing column value" : difference.Property });
                }
            }
            else
                foreach (var (a, b) in TableAlignment.Align(referenceRow.Cells, currentRow.Cells, CellValue))
                    differences.AddRange(CompareTableCell(a, b));
        }

        return differences;
    }

    /// <summary>All cell text of a table, row by row; identical tables have identical text.</summary>
    private static string TableText(TableDisplayBlock table) =>
        string.Join("\n", table.Rows.Select(row => string.Join(" | ", row.Cells.Select(CellValue))));

    private static string CellValue(DocumentTableCell cell) => string.Join("\n", cell.Paragraphs.Select(p => p.Text));
    private static string RowKey(DocumentTableRow row) => row.Cells.Count == 0 ? "" : CellValue(row.Cells[0]);
    private static bool UniqueNonEmpty(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.Count > 0 && list.All(value => !string.IsNullOrWhiteSpace(value)) && list.Distinct(StringComparer.Ordinal).Count() == list.Count;
    }

    private IEnumerable<DocumentDifference> CompareTableCell(DocumentTableCell? reference, DocumentTableCell? current)
    {
        var differences = new List<DocumentDifference>();
        var referenceParagraphs = reference?.Paragraphs ?? [];
        var currentParagraphs = current?.Paragraphs ?? [];
        var paragraphCount = Math.Max(referenceParagraphs.Count, currentParagraphs.Count);

        for (var index = 0; index < paragraphCount; index++)
        {
            var referenceParagraph = index < referenceParagraphs.Count ? referenceParagraphs[index] : null;
            var currentParagraph = index < currentParagraphs.Count ? currentParagraphs[index] : null;
            if (referenceParagraph is null)
            {
                differences.Add(CreateParagraphDifference(currentParagraph!, isAdded: true));
                continue;
            }
            if (currentParagraph is null)
            {
                differences.Add(CreateParagraphDifference(referenceParagraph, isAdded: false));
                continue;
            }

            _paragraphPairs.Add(new(referenceParagraph.Index, currentParagraph.Index));
            differences.AddRange(CreateTextDifferences(referenceParagraph, currentParagraph));
            differences.AddRange(CreateFormattingDifferences(referenceParagraph, currentParagraph));
            differences.AddRange(CreateParagraphFormattingDifferences(referenceParagraph, currentParagraph));
        }

        return differences;
    }

    private DocumentDifference CreateTableDifference(
        TableDisplayBlock? reference,
        TableDisplayBlock? current,
        string property,
        string referenceValue,
        string currentValue,
        string context)
    {
        var referenceParagraph = FirstParagraph(reference);
        var currentParagraph = FirstParagraph(current);
        return new DocumentDifference(
            NextId("TBL"),
            DifferenceCategory.Table,
            property,
            referenceValue,
            currentValue,
            referenceParagraph is null ? null : BuildLocation(referenceParagraph, 0),
            currentParagraph is null ? null : BuildLocation(currentParagraph, 0),
            referenceParagraph is null ? null : new DocumentRange(referenceParagraph.Index, 0, referenceParagraph.Text.Length),
            currentParagraph is null ? null : new DocumentRange(currentParagraph.Index, 0, currentParagraph.Text.Length),
            context,
            ReferenceTableIndex: reference?.TableIndex,
            CurrentTableIndex: current?.TableIndex);
    }

    private static ParagraphBlock? FirstParagraph(TableDisplayBlock? table) => table?.Rows
        .SelectMany(row => row.Cells)
        .SelectMany(cell => cell.Paragraphs)
        .FirstOrDefault();

    private static string DescribeTable(TableDisplayBlock table) =>
        $"Table {table.TableIndex + 1}: {table.Rows.Count} row(s), {table.Rows.Sum(row => row.Cells.Count)} cell(s)";

    /// <summary>
    /// Pairs two ordered lists (paragraphs, tables or images). Identical items (same key) anchor the
    /// alignment; between anchors, similar items are paired, and leftovers on both sides are paired in
    /// order as in-place edits. Anything still unpaired exists on one side only.
    /// </summary>
    private static List<Aligned<T>> AlignItems<T>(
        IReadOnlyList<T> reference,
        IReadOnlyList<T> current,
        Func<T, string> referenceKey,
        Func<T, string> currentKey,
        Func<T, T, double> similarity) where T : class
    {
        var referenceKeys = reference.Select(referenceKey).ToList();
        var currentKeys = current.Select(currentKey).ToList();
        var matches = LongestCommonSubsequence(referenceKeys, currentKeys);
        var pairs = new List<Aligned<T>>();
        var referenceCursor = 0;
        var currentCursor = 0;

        foreach (var match in matches.Append((ReferenceIndex: reference.Count, CurrentIndex: current.Count)))
        {
            var referenceGap = reference.Skip(referenceCursor).Take(match.ReferenceIndex - referenceCursor).ToList();
            var currentGap = current.Skip(currentCursor).Take(match.CurrentIndex - currentCursor).ToList();
            pairs.AddRange(AlignGap(referenceGap, currentGap, similarity));

            if (match.ReferenceIndex < reference.Count)
                pairs.Add(new(reference[match.ReferenceIndex], current[match.CurrentIndex]));

            referenceCursor = match.ReferenceIndex + 1;
            currentCursor = match.CurrentIndex + 1;
        }

        return pairs;
    }

    /// <summary>
    /// Pairs edited items between exact matches only when they are similar, so an unrelated
    /// inserted item is reported as added instead of as a rewrite of its neighbour.
    /// </summary>
    private static List<Aligned<T>> AlignGap<T>(IReadOnlyList<T> reference, IReadOnlyList<T> current, Func<T, T, double> similarityOf) where T : class
    {
        const double minimumSimilarity = 0.4;
        var pairs = new List<Aligned<T>>();
        if (reference.Count == 0 || current.Count == 0 || reference.Count * current.Count > 10_000)
        {
            // Very large gaps keep the simple order-based pairing to bound the cost.
            var pairedCount = reference.Count == 0 || current.Count == 0 ? 0 : Math.Min(reference.Count, current.Count);
            for (var index = 0; index < pairedCount; index++)
                pairs.Add(new(reference[index], current[index]));
            pairs.AddRange(reference.Skip(pairedCount).Select(item => new Aligned<T>(item, null)));
            pairs.AddRange(current.Skip(pairedCount).Select(item => new Aligned<T>(null, item)));
            return pairs;
        }

        // Order-preserving alignment that maximises the total similarity of paired items.
        var similarity = new double[reference.Count, current.Count];
        for (var i = 0; i < reference.Count; i++)
        for (var j = 0; j < current.Count; j++)
            similarity[i, j] = similarityOf(reference[i], current[j]);
        var score = new double[reference.Count + 1, current.Count + 1];
        for (var i = reference.Count - 1; i >= 0; i--)
        for (var j = current.Count - 1; j >= 0; j--)
        {
            score[i, j] = Math.Max(score[i + 1, j], score[i, j + 1]);
            if (similarity[i, j] >= minimumSimilarity)
                score[i, j] = Math.Max(score[i, j], score[i + 1, j + 1] + similarity[i, j]);
        }

        int left = 0, right = 0;
        var removed = new List<T>();
        var added = new List<T>();
        while (left < reference.Count && right < current.Count)
        {
            if (similarity[left, right] >= minimumSimilarity &&
                score[left, right] == score[left + 1, right + 1] + similarity[left, right])
            {
                Flush();
                pairs.Add(new(reference[left++], current[right++]));
            }
            else if (score[left + 1, right] >= score[left, right + 1])
                removed.Add(reference[left++]);
            else
                added.Add(current[right++]);
        }
        removed.AddRange(reference.Skip(left));
        added.AddRange(current.Skip(right));
        Flush();
        return pairs;

        // Similar items anchor the alignment. Unmatched items left on both sides between two
        // anchors were edited in place (however heavily), so they are still compared in detail.
        void Flush()
        {
            var editedCount = Math.Min(removed.Count, added.Count);
            for (var index = 0; index < editedCount; index++)
                pairs.Add(new(removed[index], added[index]));
            pairs.AddRange(removed.Skip(editedCount).Select(item => new Aligned<T>(item, null)));
            pairs.AddRange(added.Skip(editedCount).Select(item => new Aligned<T>(null, item)));
            removed.Clear();
            added.Clear();
        }
    }

    /// <summary>0..1 similarity: shared words, or shared characters for very short text.</summary>
    private static double Similarity(string reference, string current)
    {
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(current))
            return string.IsNullOrWhiteSpace(reference) && string.IsNullOrWhiteSpace(current) ? 1 : 0;
        var referenceWords = Words(reference);
        var currentWords = Words(current);
        if (referenceWords.Count <= 2 || currentWords.Count <= 2)
        {
            var diff = new DiffPlex.Differ().CreateCharacterDiffs(reference, current, false, false);
            var unchanged = reference.Length - diff.DiffBlocks.Sum(block => block.DeleteCountA);
            return 2d * unchanged / (reference.Length + current.Length);
        }
        var remaining = currentWords.GroupBy(word => word).ToDictionary(group => group.Key, group => group.Count());
        var shared = 0;
        foreach (var word in referenceWords)
            if (remaining.TryGetValue(word, out var count) && count > 0)
            {
                remaining[word] = count - 1;
                shared++;
            }
        return 2d * shared / (referenceWords.Count + currentWords.Count);

        static List<string> Words(string text) => text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')').ToLowerInvariant())
            .Where(word => word.Length > 0)
            .ToList();
    }

    private static IReadOnlyList<(int ReferenceIndex, int CurrentIndex)> LongestCommonSubsequence(
        IReadOnlyList<string> reference,
        IReadOnlyList<string> current)
    {
        var lengths = new int[reference.Count + 1, current.Count + 1];
        for (var referenceIndex = reference.Count - 1; referenceIndex >= 0; referenceIndex--)
        for (var currentIndex = current.Count - 1; currentIndex >= 0; currentIndex--)
            lengths[referenceIndex, currentIndex] = string.Equals(reference[referenceIndex], current[currentIndex], StringComparison.Ordinal)
                ? lengths[referenceIndex + 1, currentIndex + 1] + 1
                : Math.Max(lengths[referenceIndex + 1, currentIndex], lengths[referenceIndex, currentIndex + 1]);

        var matches = new List<(int ReferenceIndex, int CurrentIndex)>();
        var referenceCursor = 0;
        var currentCursor = 0;
        while (referenceCursor < reference.Count && currentCursor < current.Count)
        {
            if (string.Equals(reference[referenceCursor], current[currentCursor], StringComparison.Ordinal))
            {
                matches.Add((referenceCursor++, currentCursor++));
            }
            else if (lengths[referenceCursor + 1, currentCursor] >= lengths[referenceCursor, currentCursor + 1])
            {
                referenceCursor++;
            }
            else
            {
                currentCursor++;
            }
        }

        return matches;
    }

    private static IEnumerable<FormattingProperty> FormattingProperties(RunFormatting reference, RunFormatting current) =>
    [
        new("Font family", reference.FontFamily, current.FontFamily),
        new("Font size", reference.FontSizeHalfPoints, current.FontSizeHalfPoints),
        new("Bold", reference.Bold, current.Bold),
        new("Italic", reference.Italic, current.Italic),
        new("Underline", reference.Underline, current.Underline),
        new("Strikethrough", reference.Strikethrough, current.Strikethrough),
        new("Subscript", reference.Subscript, current.Subscript),
        new("Superscript", reference.Superscript, current.Superscript),
        new("Font color", reference.FontColor, current.FontColor),
        new("Highlight", reference.Highlight, current.Highlight),
        new("Character spacing", reference.CharacterSpacingTwips, current.CharacterSpacingTwips)
    ];

    private static IEnumerable<ParagraphFormattingProperty> ParagraphFormattingProperties(ParagraphLayout reference, ParagraphLayout current) =>
    [
        new("Paragraph style", reference.StyleId, current.StyleId),
        new("Paragraph alignment", NormalizeAlignment(reference.Alignment), NormalizeAlignment(current.Alignment)),
        new("Left indent", reference.LeftIndentTwips, current.LeftIndentTwips),
        new("First-line indent", reference.FirstLineIndentTwips, current.FirstLineIndentTwips),
        new("Space before", reference.SpaceBeforeTwips, current.SpaceBeforeTwips),
        new("Space after", reference.SpaceAfterTwips, current.SpaceAfterTwips),
        new("Page break before", reference.PageBreakBefore, current.PageBreakBefore)
    ];

    private static string CalculateSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ImageHash(DocumentImage? image) => image is null
        ? "(no corresponding image)"
        : Convert.ToHexString(SHA256.HashData(image.Data));

    private static string DisplayEmu(long? value) => value is null
        ? "(not specified)"
        : $"{value} EMU";

    private static string DisplayImageProperty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(not specified)" : value;

    private static string DescribeImage(DocumentImage? image) => image is null
        ? "(no corresponding image)"
        : $"{image.ContentType}; {DisplayEmu(image.WidthEmu)} x {DisplayEmu(image.HeightEmu)}; SHA-256 {ImageHash(image)}";

    private static string Slice(string value, int start, int length) =>
        length == 0 ? string.Empty : value.Substring(start, length);

    private static string DisplayValue(object? value) => value switch
    {
        null => "(not specified)",
        "" => "(no corresponding content)",
        bool flag => flag ? "On" : "Off",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "(not specified)"
    };

    private static string DisplayFormattingValue(string property, object? value) =>
        value is null && property is "Bold" or "Italic" or "Strikethrough" or "Subscript" or "Superscript"
            ? "Off"
            : DisplayValue(value);

    private static string DisplayParagraphFormattingValue(object? value) => value switch
    {
        null => "Default",
        bool flag => flag ? "On" : "Off",
        int twips => $"{twips} twips",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "Default"
    };

    private static string NormalizeAlignment(string? alignment) => string.IsNullOrWhiteSpace(alignment)
        ? "left"
        : alignment.ToLowerInvariant();

    private static string Nearby(string referenceText, string currentText) =>
        $"Reference: {Trim(referenceText)} | Current: {Trim(currentText)}";

    private static string Trim(string value) => value.Length <= 120 ? value : $"{value[..117]}...";

    private static int StartOfRun(ParagraphBlock paragraph, int runIndex) =>
        paragraph.Runs.Take(runIndex).Sum(run => run.Text.Length);

    private static DocumentLocation BuildLocation(ParagraphBlock paragraph, int characterIndex)
    {
        var cursor = 0;
        foreach (var run in paragraph.Runs)
        {
            if (characterIndex <= cursor + run.Text.Length)
                return new DocumentLocation(paragraph.Index, run.Index, paragraph.TableLocation);
            cursor += run.Text.Length;
        }

        return new DocumentLocation(paragraph.Index, paragraph.Runs.LastOrDefault()?.Index, paragraph.TableLocation);
    }

    private string NextId(string prefix) => $"{prefix}-{++_differenceNumber:000}";

    private sealed record Aligned<T>(T? Reference, T? Current) where T : class
    {
        public void Deconstruct(out T? reference, out T? current) => (reference, current) = (Reference, Current);
    }
    private sealed record FormattingProperty(string Name, object? Reference, object? Current);
    private sealed record ParagraphFormattingProperty(string Name, object? Reference, object? Current);
}
