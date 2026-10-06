using System.Text.RegularExpressions;
using DiffPlex;
using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.Comparison;

public sealed record ImageTableComparison(
    IReadOnlyList<DocumentDifference> Differences,
    IReadOnlySet<string> ReferenceImages, IReadOnlySet<string> CurrentImages,
    IReadOnlySet<int> ReferenceTables, IReadOnlySet<int> CurrentTables,
    IReadOnlySet<int> ReferenceParagraphs, IReadOnlySet<int> CurrentParagraphs);

/// <summary>Only pairs an unambiguous image/table replacement within the same named section.</summary>
public static class ImageTableComparer
{
    private const string Review = "Read from an image using OCR. Verify the recognized value against the image; OCR cannot certify an exact match.";

    public static ImageTableComparison Compare(DocumentModel reference, DocumentModel current, IImageTextReader reader)
    {
        var differences = new List<DocumentDifference>();
        var leftImages = new HashSet<string>(); var rightImages = new HashSet<string>();
        var leftTables = new HashSet<int>(); var rightTables = new HashSet<int>();
        var leftParagraphs = new HashSet<int>(); var rightParagraphs = new HashSet<int>();
        var leftSections = Sections(reference); var rightSections = Sections(current);
        foreach (var left in leftSections)
        {
            var right = rightSections.SingleOrDefault(section => section.Name == left.Name);
            if (right is null) continue;
            if (left.Images.Count == 1 && left.Tables.Count == 0 && right.Images.Count == 0 && right.Tables.Count > 0)
                Pair(left.Images[0], right, left, true);
            else if (right.Images.Count == 1 && right.Tables.Count == 0 && left.Images.Count == 0 && left.Tables.Count > 0)
                Pair(right.Images[0], left, right, false);
        }
        return new(differences, leftImages, rightImages, leftTables, rightTables, leftParagraphs, rightParagraphs);

        void Pair(DocumentImage image, Section tableSection, Section imageSection, bool imageIsReference)
        {
            var section = tableSection.Name;
            var table = tableSection.Tables[0];
            (imageIsReference ? leftImages : rightImages).Add(image.Id);
            foreach (var item in tableSection.Tables)
                (imageIsReference ? rightTables : leftTables).Add(item.TableIndex);
            var sharedText = imageSection.Paragraphs.Select(p => p.Text).ToHashSet(StringComparer.Ordinal);
            var contentParagraphs = tableSection.Paragraphs.Where(p => p.IsInTable || !sharedText.Contains(p.Text)).ToList();
            foreach (var paragraph in contentParagraphs.Where(p => !p.IsInTable))
                (imageIsReference ? rightParagraphs : leftParagraphs).Add(paragraph.Index);
            var ocr = reader.Read(image.Data);
            var first = table.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Paragraphs).FirstOrDefault();
            var imageLocation = new DocumentLocation(image.ParagraphIndex, image.RunIndex);
            var tableLocation = first is null ? null : new DocumentLocation(first.Index, null, first.TableLocation);
            differences.Add(new DocumentDifference($"MIX-{differences.Count + 1:000}", DifferenceCategory.Image,
                "Image / table representation", imageIsReference ? "Image" : "Editable table", imageIsReference ? "Editable table" : "Image",
                imageIsReference ? imageLocation : tableLocation, imageIsReference ? tableLocation : imageLocation,
                null, null, $"Section: {section}. The content representation changed.",
                imageIsReference ? image.Id : null, imageIsReference ? null : image.Id,
                imageIsReference ? null : table.TableIndex, imageIsReference ? table.TableIndex : null,
                ReviewNote: ocr.Error ?? (ocr.Words.Count == 0 ? "No image text was recognized. Data comparison needs manual review." : Review),
                ReferenceContentKind: imageIsReference ? DifferenceContentKind.Image : DifferenceContentKind.Table,
                CurrentContentKind: imageIsReference ? DifferenceContentKind.Table : DifferenceContentKind.Image));
            if (ocr.Error is not null || ocr.Words.Count == 0) return;

            var grid = ImageTableRows.Read(ocr.Words, tableSection.Tables);
            if (grid.Cells.Count > 0)
            {
                var changed = 0;
                foreach (var cell in grid.Cells)
                {
                    var value = ImageTableRows.CellText(cell.Cell);
                    if (value == cell.Text) continue;
                    changed++;
                    var paragraph = cell.Cell.Paragraphs.FirstOrDefault();
                    var location = paragraph is null ? null : new DocumentLocation(paragraph.Index, null, paragraph.TableLocation);
                    var range = paragraph is null ? null : new DocumentRange(paragraph.Index, 0, paragraph.Text.Length);
                    differences.Add(new($"MIX-{differences.Count + 1:000}", DifferenceCategory.Image, "Image / table value (OCR)",
                        imageIsReference ? cell.Text : value, imageIsReference ? value : cell.Text,
                        imageIsReference ? imageLocation : location, imageIsReference ? location : imageLocation,
                        imageIsReference ? null : range, imageIsReference ? range : null,
                        $"Section: {section}. Row {paragraph?.TableLocation?.RowIndex + 1}, column {paragraph?.TableLocation?.CellIndex + 1}.",
                        imageIsReference ? image.Id : null, imageIsReference ? null : image.Id,
                        ImageRegion: imageIsReference ? cell.Bounds : null, CurrentImageRegion: imageIsReference ? null : cell.Bounds,
                        IsRecognizedText: true, ReviewNote: Review,
                        ReferenceContentKind: imageIsReference ? DifferenceContentKind.Image : DifferenceContentKind.Table,
                        CurrentContentKind: imageIsReference ? DifferenceContentKind.Table : DifferenceContentKind.Image));
                }
                var representation = differences.First(d => d.ReferenceImageId == (imageIsReference ? image.Id : null) &&
                    d.CurrentImageId == (imageIsReference ? null : image.Id) && d.Property == "Image / table representation");
                var index = differences.IndexOf(representation);
                differences[index] = representation with { NearbyText = representation.NearbyText +
                    (changed == 0 ? " Recognized words match in mapped cells." : $" {changed} mapped cell value(s) differ in recognition.") +
                    $" {grid.UnresolvedRows} row(s) could not be mapped reliably. Unmapped headers, wrapped cells, prose, spacing and layout require review." };
                return;
            }

            // Complex layouts without reliable row anchors must not produce arbitrary value pairings.
            if (tableSection.Tables.Count > 1 || tableSection.Tables.Sum(t => t.Rows.Count) > 1)
            {
                var last = differences[^1];
                differences[^1] = last with { ReviewNote = "Could not reliably align image rows to table cells. Review the original image and tables; no value-level match is claimed." };
                return;
            }

            // Preserve OCR reading order and every table token's original location.
            var imageTokens = ocr.Words.SelectMany(word => Regex.Matches(word.Text, @"\S+")
                .Select(match => new Token(match.Value, word.Bounds, null, null))).ToList();
            var tableTokens = contentParagraphs
                .SelectMany(paragraph => Regex.Matches(paragraph.Text, @"\S+").Select(match =>
                    new Token(match.Value, null, new(paragraph.Index, match.Index, match.Length),
                        new(paragraph.Index, null, paragraph.TableLocation)))).ToList();
            var before = imageIsReference ? imageTokens : tableTokens;
            var after = imageIsReference ? tableTokens : imageTokens;
            var blocks = new Differ().CreateLineDiffs(string.Join('\n', before.Select(t => t.Text)),
                string.Join('\n', after.Select(t => t.Text)), false, false).DiffBlocks;
            foreach (var block in blocks)
            {
                for (var offset = 0; offset < Math.Max(block.DeleteCountA, block.InsertCountB); offset++)
                {
                    var a = offset < block.DeleteCountA ? before[block.DeleteStartA + offset] : null;
                    var b = offset < block.InsertCountB ? after[block.InsertStartB + offset] : null;
                    var imageToken = imageIsReference ? a : b;
                    differences.Add(new DocumentDifference($"MIX-{differences.Count + 1:000}", DifferenceCategory.Image,
                        a is null ? "Possible added value (OCR)" : b is null ? "Possible missing value (OCR)" : "Image / table value (OCR)",
                        a?.Text ?? "(no corresponding content)", b?.Text ?? "(no corresponding content)",
                        imageIsReference ? imageLocation : a?.Location,
                        imageIsReference ? b?.Location : imageLocation,
                        a?.Range, b?.Range, $"Section: {section}. Compare the selected value and its image crop.",
                        imageIsReference ? image.Id : null, imageIsReference ? null : image.Id,
                        ImageRegion: imageIsReference ? imageToken?.Bounds : null,
                        CurrentImageRegion: imageIsReference ? null : imageToken?.Bounds,
                        IsRecognizedText: true, ReviewNote: Review,
                        ReferenceContentKind: imageIsReference ? DifferenceContentKind.Image : DifferenceContentKind.Table,
                        CurrentContentKind: imageIsReference ? DifferenceContentKind.Table : DifferenceContentKind.Image));
                }
            }
            if (blocks.Count == 0)
            {
                var last = differences[^1];
                differences[^1] = last with { NearbyText = last.NearbyText + " Recognized words match in reading order. Spacing, visual layout and OCR accuracy still require review." };
            }
        }
    }

    private sealed record Token(string Text, ImageDifferenceRegion? Bounds, DocumentRange? Range, DocumentLocation? Location);
    private sealed record Section(string Name, List<DocumentImage> Images, List<TableDisplayBlock> Tables, List<ParagraphBlock> Paragraphs);

    private static List<Section> Sections(DocumentModel document)
    {
        var sections = new List<Section>();
        var section = new Section("Document body", [], [], []);
        sections.Add(section);
        foreach (var block in document.DisplayBlocks)
        {
            if (block is ParagraphDisplayBlock p)
            {
                if (p.Paragraph.Layout?.StyleId?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true)
                {
                    section = new(p.Paragraph.Text.Trim(), [], [], []);
                    sections.Add(section);
                }
                section.Images.AddRange(document.ImageItems.Where(image => image.ParagraphIndex == p.Paragraph.Index));
                section.Paragraphs.Add(p.Paragraph);
            }
            if (block is TableDisplayBlock table)
            {
                section.Tables.Add(table);
                section.Paragraphs.AddRange(table.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Paragraphs));
            }
        }
        // Repeated headings cannot identify an unambiguous corresponding section.
        return sections.Where(s => sections.Count(other => other.Name == s.Name) == 1).ToList();
    }
}
