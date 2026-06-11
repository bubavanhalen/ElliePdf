namespace ElliePdf.Navigation;

public static class AppNavigation
{
    public static event Action<string>? WorkspaceRequested;

    public static event Action<int>? ReaderPageRequested;

    public static void RequestWorkspace(string tag) => WorkspaceRequested?.Invoke(tag);

    public static void RequestReaderAtPage(int pageIndex) => ReaderPageRequested?.Invoke(pageIndex);
}
