using Xunit;

namespace ElliePdf.Tests;

public sealed class ReaderShellPolicyTests
{
    [Theory]
    [InlineData(1_000, ReaderSidebarPresentation.Docked, 300)]
    [InlineData(1_200, ReaderSidebarPresentation.Docked, 360)]
    [InlineData(900, ReaderSidebarPresentation.Overlay, 320)]
    [InlineData(640, ReaderSidebarPresentation.Overlay, 320)]
    [InlineData(639, ReaderSidebarPresentation.FullHeightOverlay, 639)]
    [InlineData(500, ReaderSidebarPresentation.FullHeightOverlay, 500)]
    public void Resolve_uses_normative_sidebar_breakpoints(
        double width,
        ReaderSidebarPresentation expectedPresentation,
        double expectedSidebarWidth)
    {
        var result = ReaderShellPolicy.Resolve(width, 700);

        Assert.Equal(expectedPresentation, result.SidebarPresentation);
        Assert.Equal(expectedSidebarWidth, result.SidebarWidth, precision: 3);
    }

    [Theory]
    [InlineData(500, 320, false)]
    [InlineData(499, 320, true)]
    [InlineData(500, 319, true)]
    public void Resolve_reports_the_supported_minimum_client_size(double width, double height, bool isBelowMinimum)
    {
        Assert.Equal(isBelowMinimum, ReaderShellPolicy.Resolve(width, height).IsBelowSupportedClientSize);
    }

    [Fact]
    public void Focus_cycle_skips_unavailable_zones_and_wraps()
    {
        static bool Available(ReaderFocusZone zone) => zone is not ReaderFocusZone.Sidebar and not ReaderFocusZone.Status;

        Assert.Equal(ReaderFocusZone.Document, ReaderFocusCycle.Move(ReaderFocusZone.Commands, false, Available));
        Assert.Equal(ReaderFocusZone.Tabs, ReaderFocusCycle.Move(ReaderFocusZone.Document, false, Available));
        Assert.Equal(ReaderFocusZone.Commands, ReaderFocusCycle.Move(ReaderFocusZone.Document, true, Available));
    }

    [Theory]
    [InlineData("S", true, false, false, ReaderShortcut.Save)]
    [InlineData("S", true, true, false, ReaderShortcut.SaveAs)]
    [InlineData("Tab", true, false, false, ReaderShortcut.NextTab)]
    [InlineData("Tab", true, true, false, ReaderShortcut.PreviousTab)]
    [InlineData("F3", false, true, false, ReaderShortcut.PreviousSearchResult)]
    [InlineData("F6", false, false, false, ReaderShortcut.CycleFocusForward)]
    [InlineData("F6", false, true, false, ReaderShortcut.CycleFocusBackward)]
    [InlineData("F11", false, false, false, ReaderShortcut.ToggleFocusMode)]
    [InlineData("PageDown", false, false, false, ReaderShortcut.ViewportForward)]
    [InlineData("Space", false, true, false, ReaderShortcut.ViewportBackward)]
    [InlineData("Home", true, false, false, ReaderShortcut.FirstPage)]
    [InlineData("End", false, false, false, ReaderShortcut.ScrollEnd)]
    [InlineData("T", false, false, false, ReaderShortcut.AddTextAnnotation)]
    [InlineData("S", false, true, false, ReaderShortcut.AddSignatureAnnotation)]
    public void Shortcut_map_matches_normative_behavior(
        string key,
        bool control,
        bool shift,
        bool isTextEditing,
        ReaderShortcut expected)
    {
        Assert.Equal(expected, ReaderShortcutMap.Resolve(key, control, shift, isTextEditing));
    }

    [Theory]
    [InlineData("C")]
    [InlineData("Home")]
    [InlineData("Z")]
    public void Shortcut_map_preserves_standard_text_editing_keys(string key)
    {
        Assert.Equal(ReaderShortcut.None, ReaderShortcutMap.Resolve(key, control: true, shift: false, isTextEditing: true));
    }

    [Theory]
    [InlineData("Space")]
    [InlineData("Home")]
    [InlineData("PageDown")]
    public void Shortcut_map_preserves_unmodified_text_navigation(string key)
    {
        Assert.Equal(ReaderShortcut.None, ReaderShortcutMap.Resolve(key, control: false, shift: false, isTextEditing: true));
    }
}
