namespace ElliePdf.Navigation;

/// <summary>Window-scoped navigation messages owned by the UI composition root.</summary>
public sealed class AppNavigation
{
    public event Action<string>? WorkspaceRequested;

    public event Action<int>? ReaderPageRequested;

    public void RequestWorkspace(string tag) => WorkspaceRequested?.Invoke(tag);

    public void RequestReaderAtPage(int pageIndex) => ReaderPageRequested?.Invoke(pageIndex);
}
