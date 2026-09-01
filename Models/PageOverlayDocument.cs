namespace ElliePdf.Models;

public sealed class PageOverlayDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Dictionary<int, PageOverlayState> Pages { get; set; } = new();

    public List<FormRecoveryEdit> FormEdits { get; set; } = [];
}

public sealed class RecoveryEnvelope
{
    public const string ExpectedMagic = "ElliePdf.Recovery";
    public const int CurrentSchemaVersion = 1;

    public string Magic { get; set; } = ExpectedMagic;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid DocumentId { get; set; }

    public long ContentRevision { get; set; }

    public string SourcePathHash { get; set; } = string.Empty;

    public string? SourceFileIdentity { get; set; }

    public long SourceLength { get; set; }

    public string SourceSha256 { get; set; } = string.Empty;

    public string PayloadSha256 { get; set; } = string.Empty;

    public PageOverlayDocument Payload { get; set; } = new();
}

public sealed class PageOverlayState
{
    public List<InkStrokeOverlay> InkStrokes { get; set; } = [];

    public List<TextOverlay> TextItems { get; set; } = [];

    public List<SignatureOverlay> Signatures { get; set; } = [];
}

public sealed class InkStrokeOverlay
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

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

/// <summary>A stable, transport-free AcroForm value used only by local crash recovery.</summary>
public sealed class FormRecoveryEdit
{
    public int PageIndex { get; set; }

    public string FieldName { get; set; } = string.Empty;

    public string WidgetType { get; set; } = string.Empty;

    public string ValueKind { get; set; } = string.Empty;

    public string? Text { get; set; }

    public bool? Boolean { get; set; }

    public List<string> Choices { get; set; } = [];
}
