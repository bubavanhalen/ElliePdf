using ElliePdf;

namespace ElliePdf.Services;

public sealed class DocumentTab
{
    public DocumentTab(PdfDocumentSession session)
    {
        Session = session;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public PdfDocumentSession Session { get; private set; }

    public string FilePath => Session.SourcePath;

    public string DisplayName => Path.GetFileName(FilePath);

    public int CurrentPageIndex { get; set; }

    public double ZoomScale { get; set; } = 1.0;

    public PdfZoomMode ZoomMode { get; set; } = PdfZoomMode.FitWidth;

    public bool IsDirty { get; set; }

    internal PdfDocumentSession ReplaceSession(PdfDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(session.IsClosed, session);

        var previous = Session;
        Session = session;
        CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, session.PageCount - 1));
        return previous;
    }
}
