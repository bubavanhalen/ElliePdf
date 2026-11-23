using Xunit;

namespace ElliePdf.PackagingTests;

public sealed class PackageLifecycleContractTests
{
    [Fact]
    public void LifecycleHarnessIsExplicitlyGuardedAndFailClosed()
    {
        var script = File.ReadAllText(RepoFile("eng", "Invoke-PackageSmoke.ps1"));
        Assert.Contains("[switch]$Execute", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$AllowDestructive", script, StringComparison.Ordinal);
        Assert.Contains("ELLIEPDF_PACKAGE_TEST_VM", script, StringComparison.Ordinal);
        Assert.Contains("already installed", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resolve-ExactFile", script, StringComparison.Ordinal);
        Assert.Contains("Safe mode", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleHarnessCoversTheReleaseMatrixAndOfflineGateIsExternal()
    {
        var script = File.ReadAllText(RepoFile("eng", "Invoke-PackageSmoke.ps1"));
        foreach (var contract in new[]
        {
            "Add-AppxPackage -Path $previousPath",
            "Add-AppxPackage -Path $currentPath",
            "Add-AppxPackage -Path $rotationPath",
            "Add-AppxPackage -Path $rollbackPath",
            "Older package downgrade unexpectedly succeeded",
            "ActivateForFile",
            "settings/recovery marker",
            "Remove-AppxPackage -Package $installedForRemoval.PackageFullName",
            "Get-AuthenticodeSignature",
            "SignerCertificate.Thumbprint"
        }) Assert.Contains(contract, script, StringComparison.OrdinalIgnoreCase);

        Assert.True(
            script.IndexOf("Add-AppxPackage -Path $rollbackPath", StringComparison.Ordinal) <
            script.IndexOf("Add-AppxPackage -Path $rotationPath", StringComparison.Ordinal),
            "Forward rollback must be installed before the still-newer certificate-rotation package.");

        var procedure = File.ReadAllText(RepoFile("eng", "PACKAGE-LIFECYCLE-PROCEDURE.md"));
        Assert.Contains("packet-capture/firewall gate", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("real operator-signed packages", procedure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LifecycleWorkflowUsesAProtectedDisposableVmAndFourSignedGenerations()
    {
        var workflow = File.ReadAllText(RepoFile(".github", "workflows", "package-lifecycle.yml"));

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule:", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: package-lifecycle", workflow, StringComparison.Ordinal);
        Assert.Contains("elliepdf-package-vm", workflow, StringComparison.Ordinal);
        Assert.Contains("ELLIEPDF_PACKAGE_TEST_VM: '1'", workflow, StringComparison.Ordinal);
        foreach (var generation in new[] { "previous", "current", "rollback", "rotation" })
        {
            Assert.Contains($"{generation}_run_id:", workflow, StringComparison.Ordinal);
        }
        Assert.Contains("-Execute -AllowDestructive", workflow, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ github.sha }}", workflow, StringComparison.Ordinal);
        Assert.Contains("0x80073D06", File.ReadAllText(RepoFile("eng", "Invoke-PackageSmoke.ps1")), StringComparison.OrdinalIgnoreCase);
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
