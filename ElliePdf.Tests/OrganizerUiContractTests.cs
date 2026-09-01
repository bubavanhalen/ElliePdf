using Xunit;

namespace ElliePdf.Tests;

public sealed class OrganizerUiContractTests
{
    [Fact]
    public void OverwriteIsAnAdvancedConfirmedPathSeparateFromSaveAs()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "OrganizePage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "OrganizePage.xaml.cs"));

        var secondaryStart = xaml.IndexOf("<CommandBar.SecondaryCommands>", StringComparison.Ordinal);
        var secondaryEnd = xaml.IndexOf("</CommandBar.SecondaryCommands>", StringComparison.Ordinal);
        Assert.True(secondaryStart >= 0 && secondaryEnd > secondaryStart);
        Assert.Contains(
            "x:Uid=\"Organize_OverwriteAdvanced\"",
            xaml[secondaryStart..secondaryEnd],
            StringComparison.Ordinal);

        var confirmation = code.IndexOf("await confirmation.ShowAsync()", StringComparison.Ordinal);
        var overwrite = code.IndexOf("ViewModel.OverwriteDocumentsAsync(file.Path)", StringComparison.Ordinal);
        Assert.True(confirmation >= 0 && overwrite > confirmation);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SaveDocumentsAsAsync(file.Path)", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
