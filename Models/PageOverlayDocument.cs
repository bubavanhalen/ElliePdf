namespace ElliePdf.Models;

public sealed class PageOverlayDocument
{
    public Dictionary<int, PageOverlayState> Pages { get; set; } = new();
}

public sealed class PageOverlayState
{
    public List<InkStrokeOverlay> InkStrokes { get; set; } = [];

    public List<TextOverlay> TextItems { get; set; } = [];

    public List<SignatureOverlay> Signatures { get; set; } = [];
}

public sealed class InkStrokeOverlay
{
    public List<PointOverlay> Points { get; set; } = [];

    public string ColorHex { get; set; } = "#000000";

    public double Thickness { get; set; } = 2;
}

public sealed class PointOverlay
{
    public double X { get; set; }

    public double Y { get; set; }
}

public sealed class TextOverlay
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public double X { get; set; }

    public double Y { get; set; }

    public string Text { get; set; } = string.Empty;

    public double FontSize { get; set; } = 14;

    public double Width { get; set; } = 220;

    public double Height { get; set; } = 44;

    public string ColorHex { get; set; } = "#000000";

    public bool IsBold { get; set; }

    public bool IsItalic { get; set; }
}

public sealed class SignatureOverlay
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public double X { get; set; }

    public double Y { get; set; }

    public string ImageBase64 { get; set; } = string.Empty;

    public double Width { get; set; } = 150;

    public double Height { get; set; } = 75;
}
