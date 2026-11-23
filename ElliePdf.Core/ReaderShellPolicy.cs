namespace ElliePdf;

/// <summary>
/// Window-width policy for the reader chrome. Values are expressed in effective pixels.
/// Keeping this policy outside WinUI makes the supported breakpoints deterministic and testable.
/// </summary>
public static class ReaderShellPolicy
{
    public const double MinimumWidth = 500;
    public const double MinimumHeight = 320;
    public const double DockedSidebarMinimumWidth = 280;
    public const double DockedSidebarMaximumWidth = 360;
    public const double OverlaySidebarWidth = 320;

    public static ReaderShellLayout Resolve(double width, double height)
    {
        var safeWidth = double.IsFinite(width) ? Math.Max(0, width) : 0;
        var safeHeight = double.IsFinite(height) ? Math.Max(0, height) : 0;

        if (safeWidth >= 1_000)
        {
            var sidebarWidth = Math.Clamp(safeWidth * 0.3, DockedSidebarMinimumWidth, DockedSidebarMaximumWidth);
            return new(
                ReaderSidebarPresentation.Docked,
                sidebarWidth,
                safeHeight,
                IsBelowSupportedClientSize(safeWidth, safeHeight));
        }

        if (safeWidth >= 640)
        {
            return new(
                ReaderSidebarPresentation.Overlay,
                OverlaySidebarWidth,
                safeHeight,
                IsBelowSupportedClientSize(safeWidth, safeHeight));
        }

        return new(
            ReaderSidebarPresentation.FullHeightOverlay,
            safeWidth,
            safeHeight,
            IsBelowSupportedClientSize(safeWidth, safeHeight));
    }

    private static bool IsBelowSupportedClientSize(double width, double height) =>
        width < MinimumWidth || height < MinimumHeight;
}

public enum ReaderSidebarPresentation
{
    Docked,
    Overlay,
    FullHeightOverlay
}

public readonly record struct ReaderShellLayout(
    ReaderSidebarPresentation SidebarPresentation,
    double SidebarWidth,
    double SidebarHeight,
    bool IsBelowSupportedClientSize)
{
    public bool ReservesDocumentSpace => SidebarPresentation == ReaderSidebarPresentation.Docked;
}

public enum ReaderFocusZone
{
    Tabs,
    Commands,
    Sidebar,
    Document,
    Status
}

public static class ReaderFocusCycle
{
    private static readonly ReaderFocusZone[] OrderedZones =
    [
        ReaderFocusZone.Tabs,
        ReaderFocusZone.Commands,
        ReaderFocusZone.Sidebar,
        ReaderFocusZone.Document,
        ReaderFocusZone.Status
    ];

    public static ReaderFocusZone Move(ReaderFocusZone current, bool reverse, Func<ReaderFocusZone, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);

        var currentIndex = Array.IndexOf(OrderedZones, current);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        for (var offset = 1; offset <= OrderedZones.Length; offset++)
        {
            var direction = reverse ? -offset : offset;
            var index = (currentIndex + direction + OrderedZones.Length * 2) % OrderedZones.Length;
            if (isAvailable(OrderedZones[index]))
            {
                return OrderedZones[index];
            }
        }

        return current;
    }
}

public enum ReaderShortcut
{
    None,
    Open,
    CloseTab,
    Save,
    SaveAs,
    NextTab,
    PreviousTab,
    GoToPage,
    Find,
    NextSearchResult,
    PreviousSearchResult,
    Print,
    FirstPage,
    LastPage,
    ScrollHome,
    ScrollEnd,
    ViewportBackward,
    ViewportForward,
    CopySelection,
    ZoomIn,
    ZoomOut,
    AddTextAnnotation,
    AddSignatureAnnotation,
    CycleFocusForward,
    CycleFocusBackward,
    ToggleFocusMode,
    DismissTransient
}

/// <summary>
/// Platform-neutral shortcut map used by the WinUI key handler and tests.
/// </summary>
public static class ReaderShortcutMap
{
    public static ReaderShortcut Resolve(string key, bool control, bool shift, bool isTextEditing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var normalizedKey = key.Trim().ToUpperInvariant();

        // Text/form controls keep their standard editing keys. Global navigation keys remain available.
        if (isTextEditing && control && normalizedKey is "A" or "C" or "V" or "X" or "Y" or "Z" or "HOME" or "END")
        {
            return ReaderShortcut.None;
        }

        if (normalizedKey == "F6")
        {
            return shift ? ReaderShortcut.CycleFocusBackward : ReaderShortcut.CycleFocusForward;
        }

        if (normalizedKey == "F11")
        {
            return ReaderShortcut.ToggleFocusMode;
        }

        if (normalizedKey == "ESCAPE")
        {
            return ReaderShortcut.DismissTransient;
        }

        if (isTextEditing && !control && normalizedKey != "F3")
        {
            return ReaderShortcut.None;
        }

        if (normalizedKey == "F3")
        {
            return shift ? ReaderShortcut.PreviousSearchResult : ReaderShortcut.NextSearchResult;
        }

        if (control)
        {
            return normalizedKey switch
            {
                "O" => ReaderShortcut.Open,
                "W" => ReaderShortcut.CloseTab,
                "S" => shift ? ReaderShortcut.SaveAs : ReaderShortcut.Save,
                "TAB" => shift ? ReaderShortcut.PreviousTab : ReaderShortcut.NextTab,
                "G" => ReaderShortcut.GoToPage,
                "F" => ReaderShortcut.Find,
                "P" => ReaderShortcut.Print,
                "HOME" => ReaderShortcut.FirstPage,
                "END" => ReaderShortcut.LastPage,
                "C" when !isTextEditing => ReaderShortcut.CopySelection,
                "ADD" or "187" => ReaderShortcut.ZoomIn,
                "SUBTRACT" or "189" => ReaderShortcut.ZoomOut,
                _ => ReaderShortcut.None
            };
        }

        return normalizedKey switch
        {
            "T" when !shift => ReaderShortcut.AddTextAnnotation,
            "S" when shift => ReaderShortcut.AddSignatureAnnotation,
            "HOME" => ReaderShortcut.ScrollHome,
            "END" => ReaderShortcut.ScrollEnd,
            "PAGEUP" => ReaderShortcut.ViewportBackward,
            "PAGEDOWN" => ReaderShortcut.ViewportForward,
            "SPACE" => shift ? ReaderShortcut.ViewportBackward : ReaderShortcut.ViewportForward,
            _ => ReaderShortcut.None
        };
    }
}
