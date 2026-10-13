using System.Runtime.InteropServices;

namespace DocumentChecker.Engine.ExactView;

/// <summary>A character on a PDF page: its text and box in points, origin at the top-left of the page.</summary>
public sealed record PdfCharacter(char Character, double X, double Y, double Width, double Height);

/// <summary>A rectangle on a page, in points, origin at the top-left of the page.</summary>
public sealed record PageRect(int Page, double X, double Y, double Width, double Height);

/// <summary>One laid-out page: its size, a picture of it (BGRA pixels), its characters and its pictures' positions.</summary>
public sealed record PdfPage(
    double Width,
    double Height,
    int PixelWidth,
    int PixelHeight,
    byte[] Pixels,
    IReadOnlyList<PdfCharacter> Characters,
    IReadOnlyList<PageRect> Images);

/// <summary>The text of one page without its picture, for reading source PDFs quickly.</summary>
public sealed record PdfTextPage(double Width, double Height, IReadOnlyList<PdfCharacter> Characters, int PictureCount);

/// <summary>
/// Reads PDF pages with PDFium (the PDF engine used by Chrome, from the bblanchon.PDFium packages): draws each page
/// and lists every character and picture with its position. PDFium is not thread-safe, so all calls are serialized.
/// </summary>
public static class Pdfium
{
    private const string Library = "pdfium";
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>
    /// Reads every page. <paramref name="scale"/> is pixels per point for the page pictures: 2 (144 dpi) stays sharp when
    /// a page is zoomed to about twice the width of a half-screen pane.
    /// </summary>
    public static IReadOnlyList<PdfPage> ReadPages(byte[] pdf, double scale = 2) => WithDocument(pdf, document =>
    {
        var pages = new List<PdfPage>();
        var count = FPDF_GetPageCount(document);
        for (var index = 0; index < count; index++)
            pages.Add(ReadPage(document, index, scale));
        return pages;
    });

    /// <summary>One page with its picture, for example to read a scanned page with text recognition.</summary>
    public static PdfPage ReadPage(byte[] pdf, int index, double scale = 2) => WithDocument(pdf, document => ReadPage(document, index, scale));

    /// <summary>The characters of every page, without drawing the pages (much faster than <see cref="ReadPages"/>).</summary>
    public static IReadOnlyList<PdfTextPage> ReadText(byte[] pdf) => WithDocument(pdf, document =>
    {
        var pages = new List<PdfTextPage>();
        var count = FPDF_GetPageCount(document);
        for (var index = 0; index < count; index++)
        {
            var page = FPDF_LoadPage(document, index);
            if (page == IntPtr.Zero)
                throw new InvalidDataException($"Page {index + 1} of the PDF could not be read.");
            try
            {
                double height = FPDF_GetPageHeightF(page);
                pages.Add(new PdfTextPage(FPDF_GetPageWidthF(page), height, Characters(page, height), Pictures(page, index, height).Count));
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
        return pages;
    });

    private static T WithDocument<T>(byte[] pdf, Func<IntPtr, T> read)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                FPDF_InitLibrary();
                _initialized = true;
            }

            // The document reads from this memory until it is closed, so it stays pinned until then.
            var handle = GCHandle.Alloc(pdf, GCHandleType.Pinned);
            var document = FPDF_LoadMemDocument(handle.AddrOfPinnedObject(), pdf.Length, null);
            try
            {
                if (document == IntPtr.Zero)
                    throw new InvalidDataException(FPDF_GetLastError() == PasswordError
                        ? "The PDF is password-protected."
                        : "The PDF could not be opened: it is damaged or not a PDF.");
                return read(document);
            }
            finally
            {
                if (document != IntPtr.Zero)
                    FPDF_CloseDocument(document);
                handle.Free();
            }
        }
    }

    private static PdfPage ReadPage(IntPtr document, int index, double scale)
    {
        var page = FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero)
            throw new InvalidDataException($"Page {index + 1} of the PDF could not be read.");
        try
        {
            double width = FPDF_GetPageWidthF(page);
            double height = FPDF_GetPageHeightF(page);
            var pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
            var pixelHeight = Math.Max(1, (int)Math.Round(height * scale));
            return new PdfPage(width, height, pixelWidth, pixelHeight,
                Render(page, pixelWidth, pixelHeight), Characters(page, height), Pictures(page, index, height));
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    private static byte[] Render(IntPtr page, int pixelWidth, int pixelHeight)
    {
        var bitmap = FPDFBitmap_Create(pixelWidth, pixelHeight, 1);
        try
        {
            FPDFBitmap_FillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
            FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixelWidth, pixelHeight, 0, RenderAnnotations);
            var stride = FPDFBitmap_GetStride(bitmap);
            var buffer = FPDFBitmap_GetBuffer(bitmap);
            var pixels = new byte[pixelWidth * 4 * pixelHeight];
            for (var row = 0; row < pixelHeight; row++)
                Marshal.Copy(buffer + row * stride, pixels, row * pixelWidth * 4, pixelWidth * 4);
            return pixels;
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
        }
    }

    private static List<PdfCharacter> Characters(IntPtr page, double pageHeight)
    {
        var characters = new List<PdfCharacter>();
        var text = FPDFText_LoadPage(page);
        if (text == IntPtr.Zero)
            return characters;
        try
        {
            var count = FPDFText_CountChars(text);
            for (var index = 0; index < count; index++)
            {
                var code = FPDFText_GetUnicode(text, index);
                if (code == 0 || code > 0xFFFF)
                    continue;
                if (FPDFText_GetCharBox(text, index, out var left, out var right, out var bottom, out var top) == 0)
                    continue;
                characters.Add(new PdfCharacter((char)code, left, pageHeight - top, Math.Max(0, right - left), Math.Max(0, top - bottom)));
            }
        }
        finally
        {
            FPDFText_ClosePage(text);
        }
        return characters;
    }

    private static List<PageRect> Pictures(IntPtr page, int pageIndex, double pageHeight)
    {
        var pictures = new List<PageRect>();
        var count = FPDFPage_CountObjects(page);
        for (var index = 0; index < count; index++)
        {
            var item = FPDFPage_GetObject(page, index);
            if (FPDFPageObj_GetType(item) != ImageObject)
                continue;
            if (FPDFPageObj_GetBounds(item, out var left, out var bottom, out var right, out var top) != 0)
                pictures.Add(new PageRect(pageIndex, left, pageHeight - top, right - left, top - bottom));
        }
        return pictures;
    }

    private const int RenderAnnotations = 0x01;
    private const int ImageObject = 3;
    private const uint PasswordError = 4;

    [DllImport(Library)] private static extern void FPDF_InitLibrary();
    [DllImport(Library)] private static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport(Library)] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Library)] private static extern uint FPDF_GetLastError();
    [DllImport(Library)] private static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(Library)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport(Library)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Library)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Library)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Library)] private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Library)] private static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Library)] private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Library)] private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Library)] private static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Library)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Library)] private static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Library)] private static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Library)] private static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Library)] private static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Library)] private static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);
    [DllImport(Library)] private static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Library)] private static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Library)] private static extern int FPDFPageObj_GetType(IntPtr pageObject);
    [DllImport(Library)] private static extern int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);
}
