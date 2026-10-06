using PdfSharp.Fonts;

namespace DocumentChecker.Engine.Reporting;

internal sealed class LocalFontResolver : IFontResolver
{
    private const string RegularFace = "DocumentChecker-Regular";
    private const string BoldFace = "DocumentChecker-Bold";
    private readonly IReadOnlyDictionary<string, string> _fontFiles;

    public LocalFontResolver()
    {
        var regular = FindFontFile("arial.ttf", "segoeui.ttf", "Arial.ttf", "DejaVuSans.ttf");
        var bold = FindFontFile("arialbd.ttf", "segoeuib.ttf", "Arial Bold.ttf", "DejaVuSans-Bold.ttf") ?? regular;
        if (regular is null)
            throw new InvalidOperationException("No usable system font was found for PDF report generation.");

        _fontFiles = new Dictionary<string, string>
        {
            [RegularFace] = regular,
            [BoldFace] = bold!
        };
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? BoldFace : RegularFace, mustSimulateBold: false, mustSimulateItalic: isItalic);

    public byte[]? GetFont(string faceName) =>
        _fontFiles.TryGetValue(faceName, out var path) ? File.ReadAllBytes(path) : null;

    private static string? FindFontFile(params string[] names)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            @"C:\Windows\Fonts",
            "/System/Library/Fonts/Supplemental",
            "/Library/Fonts",
            "/usr/share/fonts/truetype/dejavu",
            "/usr/share/fonts/truetype/liberation2"
        }.Where(Directory.Exists);

        foreach (var folder in folders)
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
