using Avalonia.Controls;
using Avalonia.Media.Imaging;
using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;
using SixLabors.ImageSharp.Processing;

namespace DocumentChecker.Engine;

public partial class MainWindow
{
    private string DescribeLocation(DocumentDifference difference)
    {
        if (difference.Category == DifferenceCategory.Review)
        {
            var isReference = IsReferenceSideReview(difference);
            var side = isReference ? "Reference" : "Current";
            return (isReference ? difference.ReferenceLocation : difference.CurrentLocation) is null
                ? $"{side}: whole document (shown at the top of the page)"
                : $"{side}: {DescribeSideLocation(difference, isReference)}";
        }
        var reference = DescribeSideLocation(difference, true);
        var current = DescribeSideLocation(difference, false);
        return reference == current ? $"Both documents: {reference}" : $"Reference: {reference}\nCurrent: {current}";
    }

    /// <summary>Headers, footers and notes are named; paragraphs are counted as a reader counts them (see FindingText.Location).</summary>
    private string DescribeSideLocation(DocumentDifference difference, bool isReference)
    {
        var location = isReference ? difference.ReferenceLocation : difference.CurrentLocation;
        var document = isReference ? _comparisonResult?.ReferenceDocument : _comparisonResult?.CurrentDocument;
        return FindingText.Location(document, location, GetDisplayContentKind(difference, isReference) == DifferenceContentKind.Image);
    }

    /// <summary>The grid column (1-based) of a cell; merged cells to its left count by the columns they cover.</summary>
    private static int ColumnNumber(TableDisplayBlock? table, TableCellLocation cell) => FindingText.GridColumnAt(table, cell) + 1;

    private static string ColumnHeader(TableDisplayBlock? table, TableCellLocation cell) => FindingText.ColumnHeader(table, cell);

    private static DocumentPart? PartOf(DocumentModel? document, int paragraphIndex) =>
        document?.Paragraphs.FirstOrDefault(paragraph => paragraph.Index == paragraphIndex)?.Part;

    private static TableDisplayBlock? FindTable(DocumentModel? document, int tableIndex) => FindingText.FindTable(document, tableIndex);

    private string DescribeRepresentation(DocumentDifference difference)
    {
        return $"The Reference value was found in {DescribeSideRepresentation(difference, isReference: true)}. " +
            $"The Current value is in {DescribeSideRepresentation(difference, isReference: false)}.";
    }

    private string DescribeSideRepresentation(DocumentDifference difference, bool isReference)
    {
        var kind = GetDisplayContentKind(difference, isReference);
        var location = isReference ? difference.ReferenceLocation : difference.CurrentLocation;
        var document = isReference ? _comparisonResult?.ReferenceDocument : _comparisonResult?.CurrentDocument;
        if (kind == DifferenceContentKind.Image)
            return "an image";
        if (kind == DifferenceContentKind.Missing)
            return "no corresponding content";
        if (kind == DifferenceContentKind.Table && location?.TableCell is { } cell)
        {
            var table = FindTable(document, cell.TableIndex);
            var rowLabel = CellText(table?.Rows.ElementAtOrDefault(cell.RowIndex)?.Cells.FirstOrDefault());
            var columnLabel = ColumnHeader(table, cell);
            var labels = string.Join(" · ", new[] { rowLabel, columnLabel }.Where(label => !string.IsNullOrWhiteSpace(label)).Select(ShortLabel));
            return $"Table {cell.TableIndex + 1}, Row {cell.RowIndex + 1}, Column {ColumnNumber(table, cell)}" +
                (labels.Length == 0 ? string.Empty : $" ({labels})");
        }
        return kind == DifferenceContentKind.Table ? "a table" : $"Paragraph {(location?.ParagraphIndex ?? 0) + 1}";
    }

    private string? PageNote(DocumentDifference difference)
    {
        var locations = new[] { difference.ReferenceLocation, difference.CurrentLocation }.Where(location => location is not null).ToList();
        if (locations.Count == 0)
            return null;
        var referencePart = difference.ReferenceLocation is { } referenceLocation ? PartOf(_comparisonResult?.ReferenceDocument, referenceLocation.ParagraphIndex) : null;
        var currentPart = difference.CurrentLocation is { } currentLocation ? PartOf(_comparisonResult?.CurrentDocument, currentLocation.ParagraphIndex) : null;
        var part = referencePart ?? currentPart;
        if (part is not null)
            return part.Kind switch
            {
                DocumentPartKind.Header or DocumentPartKind.Footer => $"This {part.Kind.ToString().ToLowerInvariant()} repeats on every page of its section.",
                DocumentPartKind.Footnote => "Footnotes are shown at the bottom of the page where they are referenced.",
                _ => "Endnotes are shown at the end of the document."
            };
        if (locations.Any(location => location!.Page is null))
            return "A DOCX has no saved page positions. Open and save it in Word to refresh pagination. Paragraph/table locations still identify the change.";
        if (locations.Any(location => location!.Page?.Source == PageNumberSource.ExplicitBreaks))
            return "Estimated from explicit page breaks; automatic overflow may add pages. Save in Word to refresh pagination.";
        return "Physical page positions from the saved DOCX layout; repagination in Word may change them.";
    }

    private string DescribeContentLocation(DocumentDifference difference)
    {
        var location = difference.ReferenceLocation ?? difference.CurrentLocation;
        var document = difference.ReferenceLocation is null ? _comparisonResult?.CurrentDocument : _comparisonResult?.ReferenceDocument;
        var paragraph = document?.Paragraphs.FirstOrDefault(item => item.Index == location?.ParagraphIndex);
        var cell = location?.TableCell ?? paragraph?.TableLocation;
        var prefix = difference.Category == DifferenceCategory.Image ? "Image · " : string.Empty;
        if (cell is null)
        {
            var imageContext = difference.IsRecognizedText ? "\n" + ShortLabel(difference.NearbyText.Split('\n')[0]) : string.Empty;
            return prefix + (location is null ? "Document" : $"Paragraph {location.ParagraphIndex + 1}") + imageContext;
        }
        var table = FindTable(document, cell.TableIndex);
        var row = table?.Rows.ElementAtOrDefault(cell.RowIndex);
        var rowLabel = CellText(row?.Cells.FirstOrDefault());
        var columnLabel = ColumnHeader(table, cell);
        var labels = cell.RowIndex > 0
            ? $"\n{ShortLabel(rowLabel)}{(string.IsNullOrWhiteSpace(columnLabel) ? "" : $" · {ShortLabel(columnLabel)}")}" : string.Empty;
        return $"{prefix}Table {cell.TableIndex + 1} · Row {cell.RowIndex + 1} · Column {ColumnNumber(table, cell)}{labels}";
    }

    private static string CellText(DocumentTableCell? cell) => FindingText.CellText(cell);
    private static string ShortLabel(string text) => FindingText.ShortLabel(text);

    private void ClearImageDetails()
    {
        DetailReferenceImage.Source = null;
        DetailCurrentImage.Source = null;
        DetailReferenceImage.IsVisible = false;
        DetailCurrentImage.IsVisible = false;
        DetailReferenceImageFrame.IsVisible = false;
        DetailCurrentImageFrame.IsVisible = false;
        foreach (var bitmap in _detailBitmaps)
            bitmap.Dispose();
        _detailBitmaps.Clear();
    }

    private void UpdateImageDetails(DocumentDifference difference)
    {
        ClearImageDetails();
        if (difference.Category != DifferenceCategory.Image)
            return;
        AddCrop(DetailReferenceImage, DetailReferenceImageFrame, _comparisonResult?.ReferenceDocument?.ImageItems.FirstOrDefault(image => image.Id == difference.ReferenceImageId), difference.RegionFor(true));
        AddCrop(DetailCurrentImage, DetailCurrentImageFrame, _comparisonResult?.CurrentDocument?.ImageItems.FirstOrDefault(image => image.Id == difference.CurrentImageId), difference.RegionFor(false));
    }

    private void AddCrop(Image target, Border frame, DocumentImage? source, ImageDifferenceRegion? region)
    {
        if (source is null)
            return;
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load(source.Data);
            if (region is not null)
            {
                var x = Math.Clamp(region.X - 24, 0, image.Width - 1);
                var y = Math.Clamp(region.Y - 12, 0, image.Height - 1);
                var right = Math.Clamp(region.X + region.Width + 24, x + 1, image.Width);
                var bottom = Math.Clamp(region.Y + region.Height + 12, y + 1, image.Height);
                image.Mutate(context => context.Crop(new SixLabors.ImageSharp.Rectangle(x, y, right - x, bottom - y)));
            }
            using var buffer = new MemoryStream();
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, buffer);
            buffer.Position = 0;
            var bitmap = new Bitmap(buffer);
            _detailBitmaps.Add(bitmap);
            target.Source = bitmap;
            var scale = Math.Min(3d, Math.Min(130d / image.Width, 90d / image.Height));
            target.Width = image.Width * scale;
            target.Height = image.Height * scale;
            target.IsVisible = true;
            frame.IsVisible = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DetailReviewText.Text = "Image preview unavailable. Review the image in the original document.";
            DetailReviewText.IsVisible = true;
        }
    }
}
