using Xunit;

namespace ElliePdf.Tests;

public sealed class ContinuousLayoutContractTests
{
    [Fact]
    public void ReaderUsesTheIndexedVirtualizingLayout()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Pages", "ReaderPage.xaml"));
        var layout = File.ReadAllText(Path.Combine(root, "Controls", "IndexedPageLayout.cs"));

        Assert.Contains("<controls:IndexedPageLayout />", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<StackLayout Orientation=\"Vertical\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PageExtentIndex", layout, StringComparison.Ordinal);
        Assert.Contains("context.RealizationRect", layout, StringComparison.Ordinal);
        Assert.Contains("MaximumRealizedPages = 12", layout, StringComparison.Ordinal);
        Assert.Contains("ElementRealizationOptions.SuppressAutoRecycle", layout, StringComparison.Ordinal);
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
