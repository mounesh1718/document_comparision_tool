using DocumentChecker.Engine.Comparison;
using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.ExactView;

/// <summary>
/// Finds where a finding is on the laid-out pages of its document, so Exact view can mark it on the real page.
/// The document's paragraphs (as read from the .docx) are matched to the characters on the pages in reading order;
/// a finding's character range in its paragraph then becomes boxes on the page. Whitespace is ignored when matching,
/// because line breaks and spacing differ between the .docx text and the printed page.
/// </summary>
public sealed class PageLocator
{
    private const char AnySymbol = '￼';
    private const int Missing = -1;

    private readonly DocumentModel _document;
    private readonly IReadOnlyList<PdfPage> _pages;
    private readonly string _text;
    private readonly List<(int Page, PdfCharacter Box)> _boxes = [];
    // The page text without symbols, for paragraphs whose symbols the PDF does not carry as text.
    private readonly string _relaxedText;
    private readonly List<int> _relaxedToText = [];
    // For each located paragraph: the page-text position of each of its significant characters (Missing if not on the page).
    private readonly Dictionary<int, int[]> _paragraphs = [];
    private readonly Dictionary<string, PageRect> _images = [];

    public PageLocator(DocumentModel document, IReadOnlyList<PdfPage> pages)
    {
        _document = document;
        _pages = pages;
        var text = new System.Text.StringBuilder();
        for (var page = 0; page < pages.Count; page++)
            foreach (var character in pages[page].Characters)
            {
                if (!IsSignificant(character.Character))
                    continue;
                // A ligature such as "ﬁ" is one character on the page but two in the document.
                var letters = character.Character is >= 'ﬀ' and <= 'ﬆ'
                    ? character.Character.ToString().Normalize(System.Text.NormalizationForm.FormKC)
                    : character.Character.ToString();
                foreach (var letter in letters)
                {
                    text.Append(Fold(letter));
                    _boxes.Add((page, character));
                }
            }
        _text = text.ToString();
        var relaxed = new System.Text.StringBuilder();
        for (var index = 0; index < _text.Length; index++)
            if (_text[index] != AnySymbol)
            {
                relaxed.Append(_text[index]);
                _relaxedToText.Add(index);
            }
        _relaxedText = relaxed.ToString();
        LocateBody();
        LocateParts();
        LocateImages();
    }

    /// <summary>How many paragraphs were found on the pages (for diagnostics and tests).</summary>
    public int LocatedParagraphCount => _paragraphs.Count;

    /// <summary>Boxes marking a finding on this document's pages; empty when it has no visible place (for example hidden text).</summary>
    public IReadOnlyList<PageRect> Locate(DocumentDifference difference, bool isReference)
    {
        var location = isReference ? difference.ReferenceLocation : difference.CurrentLocation;
        var range = isReference ? difference.ReferenceRange : difference.CurrentRange;
        var imageId = isReference ? difference.ReferenceImageId : difference.CurrentImageId;
        var tableIndex = isReference ? difference.ReferenceTableIndex : difference.CurrentTableIndex;

        if (imageId is not null)
            return _images.TryGetValue(imageId, out var picture) ? [Region(picture, imageId, difference.RegionFor(isReference))] : [];
        // Table-level findings (row count, a whole table, a table to review, an image/table swap) mark the whole table.
        if (tableIndex is { } table && (difference.Category == DifferenceCategory.Table || range is null))
            return Table(table);
        if (range is not null)
            return Span(range.ParagraphIndex, range.Start, range.Length);
        if (location is not null)
            return WholeParagraph(location.ParagraphIndex);
        return [];
    }

    /// <summary>
    /// For content that exists only in the other document: a thin line where it would be on this document's pages,
    /// above the paragraph it precedes. Null when there is no such place.
    /// </summary>
    public PageRect? Gap(DocumentDifference difference, bool isReference)
    {
        var missingHere = difference.Category == (isReference ? DifferenceCategory.ParagraphAdded : DifferenceCategory.ParagraphRemoved);
        if (!missingHere || difference.CounterpartParagraphIndex is not { } before)
            return null;
        if (before == DocumentDifference.EndOfDocument)
        {
            var last = _paragraphs.Where(item => Paragraph(item.Key)?.Part is null && item.Value.Any(position => position >= 0))
                .OrderByDescending(item => item.Value.Max()).Select(item => item.Key).FirstOrDefault(-1);
            return last < 0 ? null : LineAt(WholeParagraph(last).LastOrDefault(), below: true);
        }
        return LineAt(WholeParagraph(before).FirstOrDefault(), below: false);
    }

    private PageRect? LineAt(PageRect? paragraph, bool below)
    {
        if (paragraph is null)
            return null;
        var (left, right) = TextColumn(paragraph.Page);
        return new PageRect(paragraph.Page, left, below ? paragraph.Y + paragraph.Height + 2 : paragraph.Y - 4, right - left, 2.5);
    }

    /// <summary>The left and right edges of the text on a page.</summary>
    private (double Left, double Right) TextColumn(int page)
    {
        var characters = _pages[page].Characters.Where(character => IsSignificant(character.Character) && character.Width > 0).ToList();
        return characters.Count == 0
            ? (72, _pages[page].Width - 72)
            : (characters.Min(character => character.X), characters.Max(character => character.X + character.Width));
    }

    private IReadOnlyList<PageRect> Span(int paragraphIndex, int start, int length)
    {
        if (Paragraph(paragraphIndex) is not { } paragraph || !_paragraphs.TryGetValue(paragraphIndex, out var positions))
            return [];
        var text = paragraph.Text;
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        var before = text[..start].Count(IsSignificant);
        var inside = text.Substring(start, length).Count(IsSignificant);
        var visible = positions.Skip(before).Take(inside).Where(position => position >= 0).ToList();
        if (visible.Count > 0)
            return Lines(visible);

        // Removed text, whitespace, or a symbol the PDF draws without text: a caret between the neighbouring characters.
        var next = positions.Skip(before + inside).FirstOrDefault(position => position >= 0, Missing);
        if (next >= 0)
        {
            var (page, box) = _boxes[next];
            return [new PageRect(page, box.X - 2, box.Y, 2, box.Height)];
        }
        var previous = positions.Take(before).LastOrDefault(position => position >= 0, Missing);
        if (previous >= 0)
        {
            var (page, box) = _boxes[previous];
            return [new PageRect(page, box.X + box.Width + 0.5, box.Y, 2, box.Height)];
        }
        return [];
    }

    private IReadOnlyList<PageRect> WholeParagraph(int paragraphIndex) =>
        _paragraphs.TryGetValue(paragraphIndex, out var positions)
            ? PerPage(Lines(positions.Where(position => position >= 0).ToList()))
            : [];

    private IReadOnlyList<PageRect> Table(int tableIndex) => PerPage(_document.Paragraphs
        .Where(paragraph => paragraph.TableLocation?.TableIndex == tableIndex)
        .SelectMany(paragraph => WholeParagraph(paragraph.Index))
        .ToList());

    /// <summary>One box per line of text for the given page-text positions (in reading order).</summary>
    private List<PageRect> Lines(IReadOnlyList<int> positions)
    {
        var lines = new List<PageRect>();
        PageRect? line = null;
        foreach (var index in positions)
        {
            var (page, box) = _boxes[index];
            if (box.Width <= 0 && box.Height <= 0)
                continue;
            var sameLine = line is not null && line.Page == page && box.X >= line.X - 1 &&
                Math.Abs(box.Y + box.Height / 2 - (line.Y + line.Height / 2)) < Math.Max(box.Height, line.Height) * 0.6;
            if (!sameLine)
            {
                if (line is not null)
                    lines.Add(line);
                line = new PageRect(page, box.X, box.Y, box.Width, box.Height);
                continue;
            }
            var left = Math.Min(line!.X, box.X);
            var top = Math.Min(line.Y, box.Y);
            line = new PageRect(page, left, top, Math.Max(line.X + line.Width, box.X + box.Width) - left, Math.Max(line.Y + line.Height, box.Y + box.Height) - top);
        }
        if (line is not null)
            lines.Add(line);
        return lines;
    }

    /// <summary>Joins boxes into one box per page.</summary>
    private static IReadOnlyList<PageRect> PerPage(IEnumerable<PageRect> boxes) => boxes
        .GroupBy(box => box.Page)
        .Select(group =>
        {
            var left = group.Min(box => box.X);
            var top = group.Min(box => box.Y);
            return new PageRect(group.Key, left, top, group.Max(box => box.X + box.Width) - left, group.Max(box => box.Y + box.Height) - top);
        })
        .ToList();

    private PageRect Region(PageRect picture, string imageId, ImageDifferenceRegion? region)
    {
        if (region is null || _document.ImageItems.FirstOrDefault(image => image.Id == imageId) is not { } image)
            return picture;
        try
        {
            var info = SixLabors.ImageSharp.Image.Identify(image.Data);
            if (info is null || info.Width == 0 || info.Height == 0)
                return picture;
            var scaleX = picture.Width / info.Width;
            var scaleY = picture.Height / info.Height;
            return new PageRect(picture.Page, picture.X + region.X * scaleX, picture.Y + region.Y * scaleY,
                Math.Max(4, region.Width * scaleX), Math.Max(4, region.Height * scaleY));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return picture;
        }
    }

    /// <summary>
    /// Body paragraphs (and their table cells) in reading order. Long paragraphs that occur exactly once anchor the
    /// match; the others are searched in order between the anchors, so a short value such as "3.0" cannot jump to an
    /// unrelated place.
    /// </summary>
    private void LocateBody()
    {
        var body = _document.Paragraphs.Where(paragraph => paragraph.Part is null)
            .Select(paragraph => (paragraph.Index, Text: Folded(paragraph.Text)))
            .Where(item => item.Text.Length > 0)
            .ToList();

        var candidates = new List<(int Order, int Position)>();
        for (var order = 0; order < body.Count; order++)
        {
            if (body[order].Text.Length < 12)
                continue;
            var first = _text.IndexOf(body[order].Text, StringComparison.Ordinal);
            if (first >= 0 && _text.IndexOf(body[order].Text, first + 1, StringComparison.Ordinal) < 0)
                candidates.Add((order, first));
        }
        var anchors = IncreasingByPosition(candidates).ToDictionary(item => item.Order, item => item.Position);

        var cursor = 0;
        for (var order = 0; order < body.Count; order++)
        {
            var (index, text) = body[order];
            if (anchors.TryGetValue(order, out var anchored))
            {
                _paragraphs[index] = Enumerable.Range(anchored, text.Length).ToArray();
                cursor = anchored + text.Length;
                continue;
            }
            var limit = anchors.Where(anchor => anchor.Key > order).Select(anchor => anchor.Value).DefaultIfEmpty(_text.Length).Min();
            if (Find(text, cursor, limit) is not { } positions)
                continue;
            _paragraphs[index] = positions;
            cursor = positions.Where(position => position >= 0).DefaultIfEmpty(cursor - 1).Max() + 1;
        }
    }

    /// <summary>Headers, footers and notes: each part in order, from its first appearance (a header repeats on every page).</summary>
    private void LocateParts()
    {
        foreach (var part in _document.Paragraphs.Where(paragraph => paragraph.Part is not null).GroupBy(paragraph => paragraph.Part!.Key))
        {
            var cursor = 0;
            foreach (var paragraph in part)
            {
                var text = Folded(paragraph.Text);
                if (text.Length == 0)
                    continue;
                if ((Find(text, cursor, _text.Length) ?? Find(text, 0, _text.Length)) is not { } positions)
                    continue;
                _paragraphs[paragraph.Index] = positions;
                cursor = positions.Where(position => position >= 0).DefaultIfEmpty(cursor - 1).Max() + 1;
            }
        }
    }

    /// <summary>
    /// The page-text positions of a paragraph's characters, searched within [from, to). An exact match is preferred;
    /// otherwise symbols are ignored, because some engines draw a symbol (such as ≥ from the Symbol font) without
    /// putting it in the page's text. Those symbols are then marked as not on the page.
    /// </summary>
    private int[]? Find(string text, int from, int to)
    {
        if (to - from >= text.Length && from < _text.Length)
        {
            var exact = _text.IndexOf(text, from, to - from, StringComparison.Ordinal);
            if (exact >= 0)
                return Enumerable.Range(exact, text.Length).ToArray();
        }
        var relaxed = text.Replace(AnySymbol.ToString(), string.Empty, StringComparison.Ordinal);
        if (relaxed.Length < 4 || relaxed.Length == text.Length)
            return null;
        var relaxedFrom = LowerBound(from);
        var relaxedTo = LowerBound(to);
        if (relaxedTo - relaxedFrom < relaxed.Length)
            return null;
        var found = _relaxedText.IndexOf(relaxed, relaxedFrom, relaxedTo - relaxedFrom, StringComparison.Ordinal);
        if (found < 0)
            return null;
        var positions = new int[text.Length];
        var next = found;
        var textPosition = _relaxedToText[found];
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != AnySymbol)
            {
                positions[index] = _relaxedToText[next++];
                textPosition = positions[index] + 1;
            }
            else if (textPosition < _text.Length && _text[textPosition] == AnySymbol)
                positions[index] = textPosition++;
            else
                positions[index] = Missing;
        }
        return positions;
    }

    /// <summary>The first position in the symbol-free text at or after a position of the full page text.</summary>
    private int LowerBound(int textPosition)
    {
        int low = 0, high = _relaxedToText.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_relaxedToText[middle] < textPosition) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    /// <summary>Pictures in document order are matched to pictures on the pages with the same shape, in order.</summary>
    private void LocateImages()
    {
        var onPages = _pages.SelectMany(page => page.Images).ToList();
        var cursor = 0;
        foreach (var image in _document.ImageItems.OrderBy(image => Paragraph(image.ParagraphIndex)?.Part is null ? 0 : 1).ThenBy(image => image.ParagraphIndex))
        {
            var isBody = Paragraph(image.ParagraphIndex)?.Part is null;
            var ratio = image.WidthEmu is > 0 && image.HeightEmu is > 0 ? (double)image.WidthEmu.Value / image.HeightEmu.Value : (double?)null;
            var start = isBody ? cursor : 0;
            for (var index = start; index < onPages.Count; index++)
            {
                var candidate = onPages[index];
                if (ratio is { } expected && candidate.Height > 0 && Math.Abs(candidate.Width / candidate.Height / expected - 1) > 0.06)
                    continue;
                _images[image.Id] = candidate;
                if (isBody)
                    cursor = index + 1;
                break;
            }
        }
    }

    /// <summary>The largest set of candidates whose page positions increase with their paragraph order.</summary>
    private static List<(int Order, int Position)> IncreasingByPosition(List<(int Order, int Position)> candidates)
    {
        var tails = new List<int>();
        var previous = new int[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (candidates[tails[middle]].Position < candidates[index].Position) low = middle + 1;
                else high = middle;
            }
            previous[index] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count) tails.Add(index);
            else tails[low] = index;
        }
        var result = new List<(int, int)>();
        for (var index = tails.Count > 0 ? tails[^1] : -1; index >= 0; index = previous[index])
            result.Add(candidates[index]);
        result.Reverse();
        return result;
    }

    private ParagraphBlock? Paragraph(int index) => _document.Paragraphs.FirstOrDefault(paragraph => paragraph.Index == index);

    private static bool IsSignificant(char character) => !char.IsWhiteSpace(character) && !char.IsControl(character) && character != '­' && character != '￾';

    /// <summary>
    /// Characters compared loosely where Word and the PDF may write the same thing differently: non-breaking hyphens
    /// become "-", and symbols outside plain ASCII (≤, °, –, characters from symbol fonts) match each other.
    /// </summary>
    private static char Fold(char character) => character switch
    {
        '‑' or '‐' => '-',
        _ when character > 127 && !char.IsLetterOrDigit(character) => AnySymbol,
        _ => character
    };

    private static string Folded(string text) => string.Concat(text.Where(IsSignificant).Select(Fold));
}
