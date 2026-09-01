using Xunit;

namespace ElliePdf.Tests;

public sealed class PreviewToolchainGovernanceTests
{
    [Fact]
    public void Preview_adr_covers_all_release_governance_controls()
    {
        var root = FindRepositoryRoot();
        var adr = File.ReadAllText(Path.Combine(root, "docs", "adr", "0001-preview-toolchain-governance.md"));

        Assert.Contains("NativeAOT", adr, StringComparison.Ordinal);
        Assert.Contains("Windows App SDK", adr, StringComparison.Ordinal);
        Assert.Contains("experimental", adr, StringComparison.Ordinal);
        Assert.Contains("weekly", adr, StringComparison.Ordinal);
        Assert.Contains("last-known-good.json", adr, StringComparison.Ordinal);
        Assert.Contains("Feature-specific fallback", adr, StringComparison.Ordinal);
        Assert.Contains("fail closed", adr, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
