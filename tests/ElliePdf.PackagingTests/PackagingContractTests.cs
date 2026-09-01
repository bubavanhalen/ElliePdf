using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace ElliePdf.PackagingTests;

public sealed class PackagingContractTests
{
    [Fact]
    public void ManifestUsesDesktopIdentityAndBothArchitectures()
    {
        var xml = XDocument.Load(RepoFile("Package.appxmanifest"));
        var identity = xml.Root!.Elements().First(element => element.Name.LocalName == "Identity");
        Assert.Matches("^[0-9A-Fa-f-]{36}$", identity.Attribute("Name")?.Value ?? string.Empty);
        Assert.Matches("^CN=", identity.Attribute("Publisher")?.Value ?? string.Empty);
        var applications = xml.Descendants().Where(element => element.Name.LocalName == "Application").ToArray();
        Assert.NotEmpty(applications);
        Assert.Contains(xml.Descendants().Select(element => element.Attribute("Name")?.Value), value => value == "Windows.Desktop");
        Assert.DoesNotContain(xml.Descendants(), element => element.Name.LocalName == "PhoneIdentity");
    }

    [Fact]
    public void StaticPayloadValidatorRequiresWorkerAndManifest()
    {
        var script = File.ReadAllText(RepoFile("eng", "Test-MsixPayload.ps1"));
        Assert.Contains("AppxManifest.xml", script, StringComparison.Ordinal);
        Assert.Contains("PdfWorker/ElliePdf.Pdfium.Worker.exe", script, StringComparison.Ordinal);
        Assert.Contains("^\\d+\\.\\d+\\.\\d+\\.\\d+$", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishScriptStagesOnlyMatchingArchitectureAndVersion()
    {
        var script = File.ReadAllText(RepoFile("eng", "Publish-ReleaseArtifacts.ps1"));
        Assert.Contains("ProcessorArchitecture", script, StringComparison.Ordinal);
        Assert.Contains("identity.Version -eq $PackageVersion", script, StringComparison.Ordinal);
        Assert.Contains("architecture $expectedArchitecture", script, StringComparison.Ordinal);
        Assert.Contains("Multiple packages with version", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestPublisherScriptBacksUpAndRestoresDevelopmentManifest()
    {
        var script = File.ReadAllText(RepoFile("eng", "Set-ManifestPublisher.ps1"));
        Assert.Contains("BackupPath", script, StringComparison.Ordinal);
        Assert.Contains("RestoreFrom", script, StringComparison.Ordinal);
        Assert.Contains("Publisher = $Publisher", script, StringComparison.Ordinal);
        Assert.Contains("Manifest restored from", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SigningScriptRequiresProtectedRunnerAndDetachedChecksumSignature()
    {
        var script = File.ReadAllText(RepoFile("eng", "Sign-ReleasePackage.ps1"));
        Assert.Contains("ELLIEPDF_RUNNER_ENVIRONMENT", script, StringComparison.Ordinal);
        Assert.Contains("ELLIEPDF_RELEASE_SIGNING", script, StringComparison.Ordinal);
        Assert.Contains("ELLIEPDF_RELEASE_ENVIRONMENT", script, StringComparison.Ordinal);
        Assert.Contains("does not exactly match manifest publisher", script, StringComparison.Ordinal);
        Assert.Contains("Package identity mismatch", script, StringComparison.Ordinal);
        Assert.Contains("1.3.6.1.5.5.7.3.3", script, StringComparison.Ordinal);
        Assert.Contains("Release input must be an unsigned MSIX", script, StringComparison.Ordinal);
        Assert.Contains("SignedCms", script, StringComparison.Ordinal);
        Assert.Contains("AppxSignature.p7x", script, StringComparison.Ordinal);
        Assert.Contains("signtool verify", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedReleaseWorkflowPinsPreviewSdkAndAvoidsStoreSubmission()
    {
        var workflow = File.ReadAllText(RepoFile(".github", "workflows", "release-signing.yml"));
        Assert.Contains("environment: release-signing", workflow, StringComparison.Ordinal);
        Assert.Contains("ELLIEPDF_PRODUCTION_IDENTITY_NAME", workflow, StringComparison.Ordinal);
        Assert.Contains("runs-on: [self-hosted, windows, x64, elliepdf-signing]", workflow, StringComparison.Ordinal);
        Assert.Contains("global-json-file: global.json", workflow, StringComparison.Ordinal);
        Assert.Contains("Invoke-ProtectedRelease.ps1", workflow, StringComparison.Ordinal);
        Assert.Matches("uses:\\s+actions/upload-artifact@[0-9a-f]{40}", workflow);
        Assert.DoesNotContain("actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("PartnerCenter", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StoreBroker", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("submission", workflow, StringComparison.OrdinalIgnoreCase);

        var releaseScript = File.ReadAllText(RepoFile("eng", "Invoke-ProtectedRelease.ps1"));
        Assert.Contains("refs/tags/$Tag^{commit}", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Checked-out commit", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Test-SourceLink.ps1", releaseScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseSymbolsRequireDeterministicOfflineSourceLinkVerification()
    {
        var properties = File.ReadAllText(RepoFile("Directory.Build.props"));
        Assert.Contains("<PublishRepositoryUrl>true</PublishRepositoryUrl>", properties, StringComparison.Ordinal);
        Assert.Contains("<EmbedUntrackedSources>true</EmbedUntrackedSources>", properties, StringComparison.Ordinal);
        Assert.Contains("<ContinuousIntegrationBuild", properties, StringComparison.Ordinal);

        var verifier = File.ReadAllText(RepoFile("eng", "SourceLinkVerifier", "Program.cs"));
        Assert.Contains("CC110556-A091-4D38-9FEC-25AB9A351A6A", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No managed portable PDB", verifier, StringComparison.Ordinal);
        Assert.Contains("canonical /_/", verifier, StringComparison.Ordinal);

        var release = File.ReadAllText(RepoFile("eng", "Invoke-ProtectedRelease.ps1"));
        Assert.Contains("Test-SourceLink.ps1", release, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalBuiltPackageHasValidIdentityAndRequiredFiles()
    {
        var packagePath = Environment.GetEnvironmentVariable("ELLIEPDF_MSIX_PATH");
        if (string.IsNullOrWhiteSpace(packagePath))
            return;
        Assert.True(File.Exists(packagePath), $"ELLIEPDF_MSIX_PATH does not exist: {packagePath}");
        using var archive = ZipFile.OpenRead(packagePath);
        var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AppxManifest.xml", names);
        Assert.Contains("Assets/AppIcon.ico", names);
        Assert.Contains("PdfWorker/ElliePdf.Pdfium.Worker.exe", names);
    }

    [Fact(Skip = "Requires operator-signed MSIX, clean Windows VM and App Certification Kit; run eng/Invoke-Wack.ps1 -Execute in the release lane.")]
    public void WackAndInstallUpgradeMatrix()
    {
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
