using DocumentChecker.Engine.Reading;

namespace DocumentChecker.Engine.ExactView;

/// <summary>A document laid out as its real pages, with the means to find each finding on them.</summary>
public sealed record ExactDocument(string Engine, IReadOnlyList<PdfPage> Pages, PageLocator Locator, string? EngineNote = null)
{
    /// <summary>
    /// Lays the .docx out with Word or LibreOffice, reads the pages, and matches them to the document as read for the
    /// comparison. Returns the reason instead when no layout engine is available or the layout fails.
    /// </summary>
    public static (ExactDocument? Document, string? Error) Load(string docxPath, DocumentModel model, string workDirectory,
        LayoutEngine engine = LayoutEngine.Word)
    {
        try
        {
            var layout = PageLayoutConverter.Convert(docxPath, workDirectory, engine);
            if (layout.Pdf is null)
                return (null, layout.Error ?? PageLayoutConverter.NoEngineMessage);
            var pages = Pdfium.ReadPages(layout.Pdf);
            return (new ExactDocument(layout.Engine!, pages, new PageLocator(model, pages), layout.Note), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (null, "The exact pages could not be prepared: " + exception.Message);
        }
    }
}
