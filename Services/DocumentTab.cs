using ElliePdf;

namespace ElliePdf.Services;

public sealed class DocumentTab
{
    public DocumentTab(PdfDocumentSession session)
    {
        Session = session;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public PdfDocumentSession Session { get; }

    public string FilePath => Session.SourcePath;

    public string DisplayName => Path.GetFileName(FilePath);

    public int CurrentPageIndex { get; set; }

    public double ZoomScale { get; set; } = 1.0;

    public PdfZoomMode ZoomMode { get; set; } = PdfZoomMode.FitWidth;

    public bool IsDirty { get; set; }
}
