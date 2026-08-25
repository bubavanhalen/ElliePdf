namespace ElliePdf.Services;

/// <summary>
/// Supplies fonts for embedded text overlays, scoped to one save.
/// </summary>
/// <remarks>
/// The PDF base-14 fonts (Helvetica and friends) use single-byte encodings, so anything outside
/// Latin-1 — Cyrillic, Greek, CJK, curly quotes, emoji — has no representable character code and
/// would be dropped silently. So Arial is embedded as a CID font with Identity-H, which covers the
/// full BMP and happens to be exactly what the edit surface renders on screen. Stock Helvetica
/// remains as a fallback if the font file cannot be read.
/// </remarks>
internal sealed class OverlayFontProvider : IDisposable
{
    private const int TrueTypeFont = 2;

    /// <summary>Latin/Cyrillic/Greek coverage, metrically matching the on-screen edit surface.</summary>
    private static readonly string[][] LatinVariants =
    [
        ["arial.ttf"],
        ["arialbd.ttf"],
        ["ariali.ttf"],
        ["arialbi.ttf"]
    ];

    /// <summary>
    /// Arial has no CJK glyphs, so text containing them needs a font that does. These all cover
    /// Latin too, which keeps mixed-script text in a single run.
    /// </summary>
    private static readonly string[] CjkCandidates =
        ["msyh.ttc", "simsun.ttc", "msjh.ttc", "meiryo.ttc", "msgothic.ttc", "malgun.ttf"];

    private static readonly string[] StockNames =
        ["Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique"];

    private static readonly Dictionary<string, byte[]?> FontFileCache = [];
    private static readonly Lock CacheLock = new();

    private readonly IntPtr _document;
    private readonly Dictionary<string, IntPtr> _fonts = [];
    private bool _disposed;

    public OverlayFontProvider(IntPtr document)
    {
        _document = document;
    }

    /// <summary>True when <paramref name="value"/> needs a font beyond Arial's coverage.</summary>
    public static bool NeedsCjkCoverage(string value)
    {
        foreach (var character in value)
        {
            if (IsCjk(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCjk(char character) =>
        character is >= '\u1100' and <= '\u11FF'      // Hangul Jamo
            or >= '\u2E80' and <= '\u303F'            // CJK radicals and punctuation
            or >= '\u3040' and <= '\u30FF'            // Kana
            or >= '\u3130' and <= '\u318F'            // Hangul compatibility Jamo
            or >= '\u3400' and <= '\u4DBF'            // CJK extension A
            or >= '\u4E00' and <= '\u9FFF'            // CJK unified ideographs
            or >= '\uAC00' and <= '\uD7AF'            // Hangul syllables
            or >= '\uF900' and <= '\uFAFF';           // CJK compatibility ideographs

    /// <summary>Creates a text object, preferring an embedded Unicode font.</summary>
    public IntPtr CreateTextObject(bool isBold, bool isItalic, bool needsCjk, float fontSize)
    {
        var styleIndex = StyleIndex(isBold, isItalic);
        var candidates = needsCjk ? CjkCandidates : LatinVariants[styleIndex];

        foreach (var candidate in candidates)
        {
            var font = ResolveFont(candidate);
            if (font == IntPtr.Zero)
            {
                continue;
            }

            var textObject = PdfiumNative.FPDFPageObj_CreateTextObj(_document, font, fontSize);
            if (textObject != IntPtr.Zero)
            {
                return textObject;
            }
        }

        return PdfiumNative.FPDFPageObj_NewTextObj(_document, StockNames[styleIndex], fontSize);
    }

    private IntPtr ResolveFont(string fileName)
    {
        if (_fonts.TryGetValue(fileName, out var cached))
        {
            return cached;
        }

        var data = LoadFontFile(fileName);
        var font = data is null
            ? IntPtr.Zero
            : PdfiumNative.FPDFText_LoadFont(_document, data, (uint)data.Length, TrueTypeFont, 1);

        _fonts[fileName] = font;
        return font;
    }

    private static byte[]? LoadFontFile(string fileName)
    {
        lock (CacheLock)
        {
            if (FontFileCache.TryGetValue(fileName, out var cached))
            {
                return cached;
            }

            byte[]? data = null;
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                    fileName);

                if (File.Exists(path))
                {
                    data = File.ReadAllBytes(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                data = null;
            }

            FontFileCache[fileName] = data;
            return data;
        }
    }

    private static int StyleIndex(bool isBold, bool isItalic) => (isBold, isItalic) switch
    {
        (true, true) => 3,
        (true, false) => 1,
        (false, true) => 2,
        _ => 0
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Text objects retain their own reference, so releasing ours here is safe.
        foreach (var font in _fonts.Values.Where(font => font != IntPtr.Zero))
        {
            PdfiumNative.FPDFFont_Close(font);
        }

        _fonts.Clear();
    }
}
