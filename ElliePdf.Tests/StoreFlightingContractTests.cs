using Xunit;

namespace ElliePdf.Tests;

public sealed class StoreFlightingContractTests
{
    private static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
        }
    }

    [Fact]
    public void StoreLaneIsManualAndFailClosed()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "eng", "Invoke-StoreFlight.ps1"));
        var workflow = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "store-flighting.yml"));

        Assert.Contains("ValidateSet('status', 'submit', 'rollout', 'halt', 'finalize')", script);
        Assert.Contains("ValidateSet('flight', 'stable')", script);
        Assert.Contains("if (-not $Execute)", script);
        Assert.Contains("ELLIEPDF_STORE_APPROVED", script);
        Assert.Contains("SignedCms", script);
        Assert.Contains("--inputDirectory", script);
        Assert.Contains("--noCommit", script);
        Assert.Contains("ReleaseTag", script);
        Assert.Contains("$submissionPrefix", script);
        Assert.Contains("environment: store-production", workflow);
        Assert.Contains("elliepdf-store", workflow);
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.DoesNotContain("push:", workflow);
        Assert.Contains("microsoft-store-apppublisher@cc9910a8d59f2eb55cbb83df0a3800cf3b5300e0", workflow);
        Assert.Contains("# v1.4", workflow);
        Assert.Contains("version: v0.4.1", workflow);
        Assert.Contains("ELLIEPDF_STORE_PRODUCT_ID", workflow);
        Assert.DoesNotContain("product_id:", workflow);
        Assert.Contains("ELLIEPDF_STORE_AUTH_CERT_THUMBPRINT", workflow);
        Assert.Contains("--certificateThumbprint", script);
        Assert.DoesNotContain("AZURE_AD_APPLICATION_SECRET", workflow);
        Assert.DoesNotContain("--clientSecret", script);
        Assert.Contains("ref: ${{ github.sha }}", workflow);
    }
}
