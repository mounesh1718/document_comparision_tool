using System.Globalization;

namespace DocumentChecker.Engine.Reading;

/// <summary>
/// The character for a <c>w:sym</c> element (Insert &gt; Symbol). Word stores a font and a character code, usually in
/// the private-use range F000-F0FF. Symbol-font codes become their Unicode characters (≤ ≥ ± µ °). Codes from other
/// symbol fonts (Wingdings and similar) keep their private-use character, so a changed symbol is still a changed character.
/// </summary>
public static class SymbolText
{
    // Adobe Symbol encoding for codes 0x20-0x7E and 0xA0-0xFE. "\0" marks a code without a Unicode equivalent.
    private const string Low = " !∀#∃%&∋()∗+,−./0123456789:;<=>?≅ΑΒΧΔΕΦΓΗΙϑΚΛΜΝΟΠΘΡΣΤΥςΩΞΨΖ[∴]⊥_‾αβχδεφγηιϕκλμνοπθρστυϖωξψζ{|}∼";
    private const string High = "€ϒ′≤⁄∞ƒ♣♦♥♠↔←↑→↓°±″≥×∝∂•÷≠≡≈…⏐⎯↵ℵℑℜ℘⊗⊕∅∩∪⊃⊇⊄⊂⊆∈∉∠∇®©™∏√⋅¬∧∨⇔⇐⇑⇒⇓◊〈®©™∑⎛⎜⎝⎡⎢⎣⎧⎨⎩⎪\0〉∫⌠⎮⌡⎞⎟⎠⎤⎥⎦⎫⎬⎭";

    public static char ToChar(string? font, string? hexCode)
    {
        if (!int.TryParse(hexCode, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) || code is < 0 or > 0xFFFF)
            return '�';
        var low = code & 0xFF;
        if (string.Equals(font, "Symbol", StringComparison.OrdinalIgnoreCase) && (code <= 0xFF || (code & 0xFF00) == 0xF000))
        {
            var mapped = low is >= 0x20 and <= 0x7E ? Low[low - 0x20]
                : low is >= 0xA0 and <= 0xFE ? High[low - 0xA0]
                : '\0';
            if (mapped != '\0')
                return mapped;
        }
        return code <= 0xFF ? (char)(0xF000 | code) : (char)code;
    }
}
