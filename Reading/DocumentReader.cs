using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DrawingWordprocessing = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace DocumentChecker.Engine.Reading;

public sealed class DocumentReader
{
    public DocumentReadResult Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DocumentReadResult.Failure("DOCX-PATH-EMPTY", "A DOCX file path is required.");

        if (!string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase))
            return DocumentReadResult.Failure("DOCX-EXTENSION-INVALID", "The selected file must have a .docx extension.");

        if (!File.Exists(path))
            return DocumentReadResult.Failure("DOCX-FILE-NOT-FOUND", "The selected DOCX file was not found.");

        try
        {
            using var document = WordprocessingDocument.Open(path, false);
            var body = document.MainDocumentPart?.Document?.Body;

            if (body is null)
                return DocumentReadResult.Failure("DOCX-BODY-MISSING", "The DOCX package does not contain a readable document body.");

            var images = new List<DocumentImage>();
            var paragraphs = new List<ParagraphBlock>();
            var blocks = new List<DocumentDisplayBlock>();
            var reviewNotes = new List<DocumentReviewItem>();
            var paragraphIndex = 0;
            var tableIndex = 0;
            var pageMap = new DocxPageMap(body, document.MainDocumentPart!);
            var main = document.MainDocumentPart!;

            var unreadBlocks = new List<string>();
            foreach (var element in body.ChildElements)
            {
                switch (element)
                {
                    case Paragraph paragraph:
                    {
                        var block = ReadParagraph(paragraph, paragraphIndex++, main, images, pageMap, reviewNotes);
                        paragraphs.Add(block);
                        blocks.Add(new ParagraphDisplayBlock(block));
                        FlushUnreadBlocks(block.Index);
                        break;
                    }
                    case Table table:
                        var tableBlock = ReadTable(table, tableIndex++, ref paragraphIndex, main, images, paragraphs, pageMap, reviewNotes);
                        blocks.Add(tableBlock);
                        FlushUnreadBlocks(tableBlock.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Paragraphs).FirstOrDefault()?.Index);
                        break;
                    case SectionProperties:
                        break;
                    default:
                        // Block-level content controls, custom XML and similar wrappers are not read.
                        if (Describe(element) is { } description)
                            unreadBlocks.Add(description);
                        break;
                }
            }
            FlushUnreadBlocks(paragraphIndex > 0 ? paragraphIndex - 1 : null);

            // Headers, footers and notes are read after the body so body paragraph numbers stay stable.
            var headers = new List<DocumentDisplayBlock>();
            var footers = new List<DocumentDisplayBlock>();
            var sections = body.Descendants<SectionProperties>().ToList();
            var readParts = new HashSet<string>();
            for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
            {
                var sectionLabel = sections.Count > 1 ? $", Section {sectionIndex + 1}" : string.Empty;
                foreach (var reference in sections[sectionIndex].Elements<HeaderReference>())
                    ReadHeaderFooter(reference.Id?.Value, reference.Type?.Value, DocumentPartKind.Header, sectionIndex, sectionLabel, headers);
                foreach (var reference in sections[sectionIndex].Elements<FooterReference>())
                    ReadHeaderFooter(reference.Id?.Value, reference.Type?.Value, DocumentPartKind.Footer, sectionIndex, sectionLabel, footers);
            }

            var footnotes = ReadNotes(main.FootnotesPart, main.FootnotesPart?.Footnotes?.Elements<Footnote>(),
                body.Descendants<FootnoteReference>().Select(reference => reference.Id?.Value), DocumentPartKind.Footnote);
            var endnotes = ReadNotes(main.EndnotesPart, main.EndnotesPart?.Endnotes?.Elements<Endnote>(),
                body.Descendants<EndnoteReference>().Select(reference => reference.Id?.Value), DocumentPartKind.Endnote);

            var allBlocks = headers.Concat(blocks).Concat(footers).Concat(footnotes).Concat(endnotes).ToList();
            return new DocumentReadResult(new DocumentModel(Path.GetFileName(path), paragraphs, images, allBlocks, reviewNotes), []);

            void ReadHeaderFooter(string? id, HeaderFooterValues? type, DocumentPartKind kind, int sectionIndex, string sectionLabel, List<DocumentDisplayBlock> target)
            {
                if (id is null || !readParts.Add(id) || main.GetPartById(id) is not OpenXmlPart part)
                    return;
                OpenXmlElement? root = part is HeaderPart header ? header.Header : (part as FooterPart)?.Footer;
                if (root is null)
                    return;
                var typeName = type == HeaderFooterValues.First ? " (first page)" : type == HeaderFooterValues.Even ? " (even pages)" : string.Empty;
                var typeKey = type == HeaderFooterValues.First ? "first" : type == HeaderFooterValues.Even ? "even" : "default";
                var documentPart = new DocumentPart(kind, $"{kind}:{sectionIndex}:{typeKey}", $"{kind}{typeName}{sectionLabel}");
                target.Add(ReadPart(root, part, documentPart));
            }

            List<DocumentDisplayBlock> ReadNotes(OpenXmlPart? part, IEnumerable<OpenXmlElement>? notes, IEnumerable<long?> referencedIds, DocumentPartKind kind)
            {
                var result = new List<DocumentDisplayBlock>();
                if (part is null || notes is null)
                    return result;
                var byId = notes.ToDictionary(note => note.GetAttribute("id", note.NamespaceUri).Value ?? string.Empty);
                var number = 0;
                // Notes are numbered in the order they are referenced in the body, as Word displays them.
                foreach (var id in referencedIds.OfType<long>().Distinct())
                {
                    if (!byId.TryGetValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var note))
                        continue;
                    number++;
                    result.Add(ReadPart(note, part, new DocumentPart(kind, $"{kind}:{number}", $"{kind} {number}")));
                }
                return result;
            }

            PartDisplayBlock ReadPart(OpenXmlElement root, OpenXmlPart owner, DocumentPart part)
            {
                var partBlocks = new List<DocumentDisplayBlock>();
                foreach (var element in root.ChildElements)
                {
                    switch (element)
                    {
                        case Paragraph paragraph:
                        {
                            var block = ReadParagraph(paragraph, paragraphIndex++, owner, images, pageMap, reviewNotes, part: part);
                            paragraphs.Add(block);
                            partBlocks.Add(new ParagraphDisplayBlock(block));
                            break;
                        }
                        case Table table:
                            partBlocks.Add(ReadTable(table, tableIndex++, ref paragraphIndex, owner, images, paragraphs, pageMap, reviewNotes, part));
                            break;
                    }
                }
                return new PartDisplayBlock(part, partBlocks);
            }

            void FlushUnreadBlocks(int? nearParagraph)
            {
                if (unreadBlocks.Count == 0)
                    return;
                reviewNotes.Add(new($"Not compared next to this paragraph: {string.Join(", ", unreadBlocks.Distinct())}. Check it in the original documents.", nearParagraph));
                unreadBlocks.Clear();
            }
        }
        catch (UnauthorizedAccessException)
        {
            return DocumentReadResult.Failure("DOCX-ACCESS-DENIED", "The DOCX file could not be opened for reading.");
        }
        catch (IOException)
        {
            return DocumentReadResult.Failure(
                "DOCX-FILE-UNAVAILABLE",
                $"{Path.GetFileName(path)} is currently unavailable. Close Microsoft Word or any other program or file preview using it, then click Compare again.");
        }
        catch (OpenXmlPackageException)
        {
            return DocumentReadResult.Failure("DOCX-PACKAGE-INVALID", "The selected file is not a valid readable DOCX package.");
        }
        catch (InvalidDataException)
        {
            return DocumentReadResult.Failure("DOCX-PACKAGE-INVALID", "The selected file is not a valid readable DOCX package.");
        }
    }

    private static ParagraphBlock ReadParagraph(
        Paragraph paragraph,
        int paragraphIndex,
        OpenXmlPart owner,
        ICollection<DocumentImage> images,
        DocxPageMap pageMap,
        ICollection<DocumentReviewItem> reviewNotes,
        TableCellLocation? tableLocation = null,
        DocumentPart? part = null)
    {
        var layout = ReadParagraphLayout(paragraph.ParagraphProperties);
        // Only headers and footers print a different page number or date on every page.
        // Body fields keep their printed value, so a changed date in the body is still reported.
        var fieldText = part?.Kind is DocumentPartKind.Header or DocumentPartKind.Footer
            ? FieldResultText(paragraph)
            : [];
        var runs = paragraph.Descendants<Run>()
            .Select((run, runIndex) => ReadRun(run, paragraphIndex, runIndex, owner, images, layout.Alignment, pageMap,
                fieldText.TryGetValue(run, out var text) ? text : null))
            .ToList();

        var unsupported = paragraph.Descendants().Select(Describe).OfType<string>().Distinct().ToList();
        if (unsupported.Count > 0)
            reviewNotes.Add(new($"Not fully compared in this paragraph: {string.Join(", ", unsupported)}. Check it in the original documents.", paragraphIndex));

        return new ParagraphBlock(paragraphIndex, runs, layout, tableLocation, pageMap.ForParagraph(paragraph), part);
    }

    /// <summary>
    /// Replacement text for runs inside header/footer fields whose printed value depends on the page or
    /// the moment of printing. Their cached values differ between files without being a content change,
    /// so the field itself is compared instead.
    /// </summary>
    private static Dictionary<Run, string> FieldResultText(Paragraph paragraph)
    {
        var result = new Dictionary<Run, string>();
        string? instruction = null;
        var inResult = false;
        var placeholderEmitted = false;
        foreach (var run in paragraph.Descendants<Run>())
        {
            if (run.Ancestors<SimpleField>().FirstOrDefault() is { } simple)
            {
                if (Placeholder(simple.Instruction?.Value) is { } simplePlaceholder)
                    result[run] = ReferenceEquals(simple.Descendants<Run>().First(), run) ? simplePlaceholder : string.Empty;
                continue;
            }
            var fieldChar = run.GetFirstChild<FieldChar>()?.FieldCharType?.Value;
            if (fieldChar == FieldCharValues.Begin)
            {
                instruction = string.Empty;
                inResult = false;
                placeholderEmitted = false;
                continue;
            }
            if (instruction is null)
                continue;
            if (fieldChar == FieldCharValues.End)
            {
                instruction = null;
                continue;
            }
            if (!inResult)
            {
                instruction += string.Concat(run.Elements<FieldCode>().Select(code => code.Text));
                if (fieldChar == FieldCharValues.Separate)
                    inResult = true;
                continue;
            }
            if (Placeholder(instruction) is { } placeholder)
            {
                result[run] = placeholderEmitted ? string.Empty : placeholder;
                placeholderEmitted = true;
            }
        }
        return result;

        static string? Placeholder(string? instruction) =>
            instruction?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() switch
            {
                "PAGE" => "[Page number]",
                "NUMPAGES" or "SECTIONPAGES" => "[Page count]",
                "DATE" or "TIME" or "SAVEDATE" or "PRINTDATE" or "EDITTIME" => "[Date/time]",
                _ => null
            };
    }

    /// <summary>A reader-friendly name for content this comparison does not verify, or null when it is supported.</summary>
    private static string? Describe(OpenXmlElement element) => element switch
    {
        DrawingWordprocessing.Anchor => "floating image position and wrapping",
        // An inline content control holds ordinary runs, which are read and compared like any other text.
        SdtRun => null,
        _ => element.LocalName switch
        {
            "txbxContent" => "text box",
            "oMath" or "oMathPara" => "equation",
            "object" or "chart" => "embedded object or chart",
            "pict" => "legacy drawing",
            "relIds" => "SmartArt diagram",
            "sdt" => "content control",
            "ins" or "del" or "moveFrom" or "moveTo" => "tracked changes",
            "customXml" => "custom XML content",
            "altChunk" => "embedded document",
            _ => null
        }
    };

    private static TableDisplayBlock ReadTable(
        Table table,
        int tableIndex,
        ref int paragraphIndex,
        OpenXmlPart owner,
        ICollection<DocumentImage> images,
        ICollection<ParagraphBlock> allParagraphs,
        DocxPageMap pageMap,
        ICollection<DocumentReviewItem> reviewNotes,
        DocumentPart? part = null)
    {
        // Merged cells and nested tables are read and compared (see below); only structure the reader skips needs review.
        var structure = new List<string>();
        if (table.Descendants().Any(element => element.LocalName is "sdt" or "customXml" && !element.Ancestors<Paragraph>().Any()))
            structure.Add("rows or cells inside content controls that were not read");
        if (structure.Count > 0)
            reviewNotes.Add(new($"This table has {string.Join(" and ", structure)}. Verify its structure and nested content manually.", TableIndex: tableIndex));

        var rows = new List<DocumentTableRow>();
        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<DocumentTableCell>();
            var rowIndex = rows.Count;
            // Rows can start after skipped grid columns (gridBefore).
            var gridColumn = row.TableRowProperties?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
            foreach (var cell in row.Elements<TableCell>())
            {
                var cellParagraphs = new List<ParagraphBlock>();
                var cellIndex = cells.Count;
                foreach (var paragraph in CellParagraphs(cell))
                {
                    var block = ReadParagraph(paragraph, paragraphIndex++, owner, images, pageMap, reviewNotes,
                        new TableCellLocation(tableIndex, rowIndex, cellIndex), part);
                    cellParagraphs.Add(block);
                    allParagraphs.Add(block);
                }
                var properties = cell.TableCellProperties;
                var columnSpan = Math.Max(1, properties?.GridSpan?.Val?.Value ?? 1);
                // <w:vMerge w:val="restart"/> starts a vertical merge; <w:vMerge/> continues the cell above.
                var verticalMerge = properties?.VerticalMerge;
                var mergedFromAbove = verticalMerge is not null &&
                    (verticalMerge.Val is null || verticalMerge.Val.Value == MergedCellValues.Continue);
                cells.Add(new DocumentTableCell(cellParagraphs, gridColumn, columnSpan, 1, mergedFromAbove));
                gridColumn += columnSpan;
            }
            rows.Add(new DocumentTableRow(cells));
        }

        // A cell that starts a vertical merge spans the continuing cells directly below it in the same column.
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var cells = rows[rowIndex].Cells.ToList();
            for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                var cell = cells[cellIndex];
                if (cell.MergedFromAbove)
                    continue;
                var rowSpan = 1;
                while (rowIndex + rowSpan < rows.Count && rows[rowIndex + rowSpan].Cells
                           .Any(below => below.MergedFromAbove && below.GridColumn == cell.GridColumn))
                    rowSpan++;
                if (rowSpan > 1)
                    cells[cellIndex] = cell with { RowSpan = rowSpan };
            }
            rows[rowIndex] = new DocumentTableRow(cells);
        }

        return new TableDisplayBlock(rows, tableIndex, part);
    }

    /// <summary>
    /// The paragraphs of a cell in reading order. A table nested in the cell contributes its paragraphs
    /// row by row, so its text is compared and shown as lines of the outer cell.
    /// </summary>
    private static IEnumerable<Paragraph> CellParagraphs(TableCell cell)
    {
        foreach (var element in cell.ChildElements)
        {
            if (element is Paragraph paragraph)
                yield return paragraph;
            else if (element is Table nested)
                foreach (var nestedParagraph in nested.Descendants<Paragraph>())
                    yield return nestedParagraph;
        }
    }

    private static TextRun ReadRun(
        Run run,
        int paragraphIndex,
        int runIndex,
        OpenXmlPart owner,
        ICollection<DocumentImage> images,
        string? paragraphAlignment,
        DocxPageMap pageMap,
        string? textOverride)
    {
        var imageIds = new List<string>();
        foreach (var blip in run.Descendants<DocumentFormat.OpenXml.Drawing.Blip>())
        {
            var relationshipId = blip.Embed?.Value;
            if (string.IsNullOrWhiteSpace(relationshipId))
                continue;

            // Image relationships belong to the part that contains the run (body, header, footer or note).
            if (owner.GetPartById(relationshipId) is not ImagePart imagePart)
                continue;

            using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var extent = run.Descendants<DrawingWordprocessing.Inline>().FirstOrDefault()?.Extent;
            var properties = run.Descendants()
                .FirstOrDefault(element => string.Equals(element.LocalName, "docPr", StringComparison.Ordinal));
            var image = new DocumentImage(
                $"IMG-{images.Count + 1:000}",
                paragraphIndex,
                runIndex,
                buffer.ToArray(),
                imagePart.ContentType,
                extent?.Cx?.Value,
                extent?.Cy?.Value,
                GetAttributeValue(properties, "descr"),
                GetAttributeValue(properties, "title"),
                paragraphAlignment,
                relationshipId,
                pageMap.ForImage(blip));
            images.Add(image);
            imageIds.Add(image.Id);
        }

        return new TextRun(runIndex, textOverride ?? ReadRunText(run), ReadFormatting(run.RunProperties), imageIds);
    }

    private static string? GetAttributeValue(OpenXmlElement? element, string name)
    {
        var value = element?.GetAttributes().FirstOrDefault(attribute =>
            string.Equals(attribute.LocalName, name, StringComparison.Ordinal)).Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string ReadRunText(Run run)
    {
        var text = new System.Text.StringBuilder();

        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case Text value:
                    text.Append(value.Text);
                    break;
                case TabChar:
                    text.Append('\t');
                    break;
                case Break:
                case CarriageReturn:
                    text.Append('\n');
                    break;
                case NoBreakHyphen:
                    text.Append('\u2011');
                    break;
                case SoftHyphen:
                    text.Append('\u00AD');
                    break;
                case SymbolChar symbol:
                    text.Append(SymbolText.ToChar(symbol.Font?.Value, symbol.Char?.Value));
                    break;
            }
        }

        return text.ToString();
    }

    private static RunFormatting ReadFormatting(RunProperties? properties)
    {
        var fonts = properties?.RunFonts;
        var verticalAlignment = properties?.VerticalTextAlignment?.Val?.Value;

        return new RunFormatting(
            fonts?.Ascii?.Value ?? fonts?.HighAnsi?.Value,
            properties?.FontSize?.Val?.Value,
            ReadOnOff(properties?.Bold),
            ReadOnOff(properties?.Italic),
            properties?.Underline?.Val?.InnerText,
            ReadOnOff(properties?.Strike),
            verticalAlignment == VerticalPositionValues.Subscript ? true : verticalAlignment is null ? null : false,
            verticalAlignment == VerticalPositionValues.Superscript ? true : verticalAlignment is null ? null : false,
            properties?.Color?.Val?.Value,
            properties?.Highlight?.Val?.InnerText,
            properties?.Spacing?.Val?.Value,
            ReadOnOff(properties?.Vanish));
    }

    private static bool? ReadOnOff(OnOffType? value) => value is null ? null : value.Val?.Value ?? true;

    private static ParagraphLayout ReadParagraphLayout(ParagraphProperties? properties)
    {
        var spacing = properties?.SpacingBetweenLines;
        var indentation = properties?.Indentation;
        return new ParagraphLayout(
            properties?.ParagraphStyleId?.Val?.Value,
            ReadJustification(properties?.Justification),
            ParseTwips(indentation?.Left?.Value),
            ParseTwips(indentation?.FirstLine?.Value),
            ParseTwips(spacing?.Before?.Value),
            ParseTwips(spacing?.After?.Value),
            ReadOnOff(properties?.PageBreakBefore) == true);
    }

    private static int? ParseTwips(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string? ReadJustification(Justification? justification)
    {
        if (justification is null)
            return null;

        var value = justification.GetAttribute("val", "http://schemas.openxmlformats.org/wordprocessingml/2006/main").Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
