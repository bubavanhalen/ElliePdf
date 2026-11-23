using System.Xml.Linq;
using Xunit;

namespace ElliePdf.UI.AutomationTests;

public sealed class UiAutomationContractTests
{
    [Fact]
    public void InteractiveProcedureDefinesKeyboardAndTouchCoverage()
    {
        var procedure = File.ReadAllText(RepoFile("eng", "UIA-PROCEDURE.md"));
        Assert.Contains("keyboard", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("touch", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Page X of Y", procedure, StringComparison.Ordinal);
        Assert.Contains("Narrator", procedure, StringComparison.Ordinal);
        Assert.Contains("copy", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visual line", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cross-page", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Accessibility Insights", procedure, StringComparison.Ordinal);
        Assert.Contains("signed install", procedure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UiAutomationScriptDefinesExecutableFailClosedWorkflow()
    {
        var script = File.ReadAllText(RepoFile("eng", "Run-UiAccessibility.ps1"));
        Assert.Contains("-Interactive -Execute", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 'uia-1.0'", script, StringComparison.Ordinal);
        Assert.Contains("AutomationElement]::AutomationIdProperty", script, StringComparison.Ordinal);
        Assert.Contains("AutomationElement]::NameProperty", script, StringComparison.Ordinal);
        Assert.Contains("ControlType]::Button", script, StringComparison.Ordinal);
        Assert.Contains("ControlType]::TabItem", script, StringComparison.Ordinal);
        Assert.Contains("SelectionItemPattern", script, StringComparison.Ordinal);
        Assert.Contains("InvokePattern", script, StringComparison.Ordinal);
        Assert.Contains("ValuePattern", script, StringComparison.Ordinal);
        Assert.Contains("Send-Keys('{TAB}')", script, StringComparison.Ordinal);
        Assert.Contains("Send-Keys('^w')", script, StringComparison.Ordinal);
        Assert.Contains("Send-Keys('^g')", script, StringComparison.Ordinal);
        Assert.Contains("'NavView'", script, StringComparison.Ordinal);
        Assert.Contains("'NavItemRead'", script, StringComparison.Ordinal);
        Assert.Contains("'NavItemSettings'", script, StringComparison.Ordinal);
        Assert.Contains("'ReaderCommandBar'", script, StringComparison.Ordinal);
        Assert.Contains("'Page number'", script, StringComparison.Ordinal);
        Assert.Contains("'Search in document'", script, StringComparison.Ordinal);
    }

    [Fact(Skip = "Requires a signed desktop build and an interactive Windows desktop session; run eng/Run-UiAccessibility.ps1 -Interactive on the dedicated UIA agent.")]
    public void InteractiveAccessibilitySmoke()
    {
        // The executable/UIA session is intentionally not started from a normal unit-test process.
        // The dedicated script records the required evidence without weakening CI isolation.
    }

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray());
    }
}
