using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocumentChecker.Engine.Reading;

/// <summary>
/// Physical page positions from the DOCX's saved layout markers or explicit breaks.
/// This does not claim to repaginate Word documents, or interpret restarted footer numbering.
/// </summary>
internal sealed class DocxPageMap
{
    private readonly Dictionary<Paragraph, IReadOnlyList<DocumentPagePosition>> _paragraphs = new();
    private readonly Dictionary<OpenXmlElement, DocumentPage> _drawings = new();

    public DocxPageMap(Body body, MainDocumentPart mainPart)
    {
        var paragraphs = body.Descendants<Paragraph>().ToList();
        var sections = body.Descendants<SectionProperties>().ToList();
        var savedLayout = body.Descendants<LastRenderedPageBreak>().Any();
        var explicitBreaks = body.Descendants<Break>().Any(IsPageBreak) || paragraphs.Any(paragraph => PageBreakBefore(paragraph, mainPart)) || sections.Count > 1;
        var savedPageCount = (mainPart.OpenXmlPackage as WordprocessingDocument)?.ExtendedFilePropertiesPart?.Properties?.Pages?.Text;
        if (!savedLayout && !explicitBreaks && savedPageCount != "1")
            return; // There is no reliable page position in this package.

        var source = savedLayout || savedPageCount == "1" && !explicitBreaks
            ? PageNumberSource.SavedLayout : PageNumberSource.ExplicitBreaks;
        var page = 1;
        var atPageStart = true;
        var explicitBreakPending = false;
        var sectionIndex = 0;
        foreach (var paragraph in paragraphs)
        {
            if (PageBreakBefore(paragraph, mainPart) && !atPageStart)
            {
                page++;
                atPageStart = true;
                explicitBreakPending = true;
            }
            var positions = new List<DocumentPagePosition> { new(0, new(page, source)) };
            var offset = 0;
            foreach (var run in paragraph.Descendants<Run>())
            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case LastRenderedPageBreak:
                        // Word may save a rendered marker immediately after an explicit break.
                        if (!explicitBreakPending)
                            page++;
                        explicitBreakPending = false;
                        atPageStart = true;
                        Mark();
                        break;
                    case Break lineBreak when IsPageBreak(lineBreak):
                        page++;
                        offset++; // Matches DocumentReader.ReadRunText's newline.
                        explicitBreakPending = true;
                        atPageStart = true;
                        Mark();
                        break;
                    case Text text:
                        offset += text.Text.Length;
                        if (text.Text.Length > 0)
                        {
                            atPageStart = false;
                            explicitBreakPending = false;
                        }
                        break;
                    case Drawing:
                        _drawings[child] = new(page, source);
                        atPageStart = false;
                        explicitBreakPending = false;
                        break;
                    case TabChar or Break or CarriageReturn or NoBreakHyphen or SoftHyphen:
                        offset++;
                        atPageStart = false;
                        explicitBreakPending = false;
                        break;
                }
            }
            _paragraphs[paragraph] = positions;

            if (paragraph.ParagraphProperties?.SectionProperties is not null)
            {
                // Section properties describe the section ending here. The next section's
                // type tells us how that next section starts.
                var next = sections.ElementAtOrDefault(++sectionIndex)?.GetFirstChild<SectionType>()?.Val?.Value;
                if (next != SectionMarkValues.Continuous && next != SectionMarkValues.NextColumn)
                {
                    if (!atPageStart)
                        page++;
                    if (next == SectionMarkValues.OddPage && page % 2 == 0 || next == SectionMarkValues.EvenPage && page % 2 != 0)
                        page++;
                    atPageStart = true;
                    explicitBreakPending = true;
                }
            }

            void Mark()
            {
                if (positions[^1].Start == offset)
                    positions[^1] = new(offset, new(page, source));
                else
                    positions.Add(new(offset, new(page, source)));
            }
        }
    }

    public IReadOnlyList<DocumentPagePosition>? ForParagraph(Paragraph paragraph) => _paragraphs.GetValueOrDefault(paragraph);
    public DocumentPage? ForImage(OpenXmlElement blip)
    {
        var drawing = blip.Ancestors<Drawing>().FirstOrDefault();
        return drawing is null ? null : _drawings.GetValueOrDefault(drawing);
    }

    private static bool IsPageBreak(Break lineBreak) => lineBreak.Type?.Value == BreakValues.Page;

    private static bool PageBreakBefore(Paragraph paragraph, MainDocumentPart mainPart)
    {
        if (paragraph.ParagraphProperties?.PageBreakBefore is { } direct)
            return direct.Val?.Value ?? true;
        var styles = mainPart.StyleDefinitionsPart?.Styles?.Elements<Style>().ToList() ?? [];
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var visited = new HashSet<string>();
        while (styleId is not null && visited.Add(styleId))
        {
            var style = styles.FirstOrDefault(candidate => candidate.StyleId?.Value == styleId);
            if (style?.StyleParagraphProperties?.GetFirstChild<PageBreakBefore>() is { } inherited)
                return inherited.Val?.Value ?? true;
            styleId = style?.BasedOn?.Val?.Value;
        }
        return false;
    }
}
