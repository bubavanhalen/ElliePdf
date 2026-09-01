using ElliePdf;

namespace ElliePdf.Services;

public interface IDocumentTabService : IDocumentSessionService
{
    IReadOnlyList<DocumentTab> Tabs { get; }

    Guid? ActiveTabId { get; }

    DocumentTab? ActiveTab { get; }

    event EventHandler? TabsChanged;

    Task<DocumentTab> OpenTabAsync(string path, bool activate = true, CancellationToken cancellationToken = default);

    Task ActivateTabAsync(Guid tabId, CancellationToken cancellationToken = default);

    Task CloseTabAsync(Guid tabId, CancellationToken cancellationToken = default);

    DocumentTab? FindTabByPath(string path);

    Task<DocumentTab> OpenOrActivateTabAsync(string path, CancellationToken cancellationToken = default);

    Task<DocumentTab> RestoreTabAsync(
        SessionTabState state,
        bool activate,
        CancellationToken cancellationToken = default);

    DocumentTab RestoreLockedPlaceholder(
        string path,
        int pageIndex,
        double zoomScale,
        PdfZoomMode zoomMode,
        bool activate);
}
