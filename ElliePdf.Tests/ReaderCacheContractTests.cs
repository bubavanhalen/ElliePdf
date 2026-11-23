using Xunit;

namespace ElliePdf.Tests;

public sealed class ReaderCacheContractTests
{
    [Fact]
    public void ReaderUsesViewportDrivenByteBoundedThumbnailAndSemanticCaches()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Pages", "ReaderPage.xaml"));
        var reader = File.ReadAllText(Path.Combine(root, "ViewModels", "ReaderViewModel.cs"));

        Assert.Contains("ContainerContentChanging=\"PageThumbnails_ContainerContentChanging\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("const int batchSize = 6", reader, StringComparison.Ordinal);
        Assert.Contains("RenderCacheBudgets.Default.ThumbnailBudgetBytes", reader, StringComparison.Ordinal);
        Assert.Contains("RenderCacheBudgets.Default.MetadataBudgetBytes", reader, StringComparison.Ordinal);
        Assert.Contains("BenchmarkThumbnailCacheBytes => _thumbnailCache.ResidentBytes", reader, StringComparison.Ordinal);
        Assert.Contains("BenchmarkGeometryCacheBytes => _semanticPageCache.ResidentBytes", reader, StringComparison.Ordinal);
        Assert.Contains("EstimateSemanticPageBytes", reader, StringComparison.Ordinal);
        Assert.Contains("controller.EvictPage(eviction.Key.PageIndex)", reader, StringComparison.Ordinal);
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
