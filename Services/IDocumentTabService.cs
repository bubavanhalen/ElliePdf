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

    /// <summary>Re-points any tab holding <paramref name="oldSession"/> at a replacement session.</summary>
    void ReplaceSession(PdfDocumentSession oldSession, PdfDocumentSession newSession);
}
