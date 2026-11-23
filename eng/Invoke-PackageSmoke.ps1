[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$PreviousPackagePath,
    [Parameter(Mandatory)][string]$RollbackPackagePath,
    [Parameter(Mandatory)][string]$CertificateRotationPackagePath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9.-]{3,50}$')][string]$ExpectedIdentityName,
    [Parameter(Mandatory)][ValidatePattern('^CN=')][string]$ExpectedPublisher,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$ExpectedArchitecture,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$ExpectedVersion,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$RotatedCertificateThumbprint,
    [switch]$Execute,
    [switch]$AllowDestructive
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail([string]$Message) { throw "PACKAGE-LIFECYCLE-FAIL: $Message" }
function Resolve-ExactFile([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { Fail "$Label was not found: $Path" }
    return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).ProviderPath
}
function Read-PackageIdentity([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) { Fail "AppxManifest.xml is missing from $Path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $xml = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $identity = $xml.Package.Identity
        if ($null -eq $identity) { Fail "Package identity is missing from $Path" }
        return [pscustomobject]@{ Name=[string]$identity.Name; Publisher=[string]$identity.Publisher; Version=[version][string]$identity.Version; VersionText=[string]$identity.Version; Architecture=([string]$identity.ProcessorArchitecture).ToLowerInvariant() }
    } finally { $zip.Dispose() }
}
function Assert-Package([string]$Path, [string]$Label, [string]$IdentityName, [string]$Publisher, [string]$Architecture) {
    if ([IO.Path]::GetExtension($Path) -cne '.msix') { Fail "$Label must be an architecture-specific .msix package." }
    & (Join-Path $PSScriptRoot 'Test-MsixPayload.ps1') -PackagePath $Path -ExpectedArchitecture $Architecture
    $identity = Read-PackageIdentity $Path
    if ($identity.Name -ne $IdentityName) { Fail "$Label identity name '$($identity.Name)' does not equal '$IdentityName'" }
    if ($identity.Publisher -ne $Publisher) { Fail "$Label publisher '$($identity.Publisher)' does not equal '$Publisher'" }
    if ($identity.Architecture -ne $Architecture) { Fail "$Label architecture '$($identity.Architecture)' does not equal '$Architecture'" }
    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) { Fail "$Label signature status is '$($signature.Status)'" }
    if ([string]$signature.SignerCertificate.Subject -cne $Publisher) { Fail "$Label signer subject does not equal the expected publisher." }
    return $identity
}
function Assert-Installed([string]$Name, [string]$Publisher, [version]$Version) {
    $installed = @(Get-AppxPackage -Name $Name -ErrorAction SilentlyContinue)
    if ($installed.Count -ne 1) { Fail "Expected exactly one installed package named $Name; found $($installed.Count)" }
    $identity = (Get-AppxPackageManifest -Package $installed[0]).Package.Identity
    if ([string]$identity.Name -ne $Name -or [string]$identity.Publisher -ne $Publisher -or [version][string]$identity.Version -ne $Version) { Fail "Installed identity does not exactly match $Name/$Publisher/$Version" }
    return $installed[0]
}
function Assert-FileActivation([object]$InstalledPackage, [string]$PdfPath) {
    if ($null -eq ('ElliePdfPackageActivation' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem { }

[ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray { }

[Flags]
internal enum ActivateOptions : uint { None = 0 }

[ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, ActivateOptions options, out uint processId);
    [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IShellItemArray itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
    [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IShellItemArray itemArray, out uint processId);
}

[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManager { }

public static class ElliePdfPackageActivation
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindingContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromShellItem(IShellItem item, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItemArray array);

    public static uint ActivateForFile(string appUserModelId, string path)
    {
        IShellItem item = null;
        IShellItemArray array = null;
        IApplicationActivationManager manager = null;
        try
        {
            var itemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out item));
            var arrayId = typeof(IShellItemArray).GUID;
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromShellItem(item, ref arrayId, out array));
            manager = (IApplicationActivationManager)new ApplicationActivationManager();
            Marshal.ThrowExceptionForHR(manager.ActivateForFile(appUserModelId, array, "open", out uint processId));
            return processId;
        }
        finally
        {
            if (manager != null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
            if (array != null && Marshal.IsComObject(array)) Marshal.FinalReleaseComObject(array);
            if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }
}
'@
    }

    $manifest = Get-AppxPackageManifest -Package $InstalledPackage
    $applications = @($manifest.Package.Applications.Application)
    if ($applications.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$applications[0].Id)) { Fail 'Installed package does not expose exactly one application ID.' }
    $appUserModelId = $InstalledPackage.PackageFamilyName + '!' + [string]$applications[0].Id
    $activatedProcessId = [ElliePdfPackageActivation]::ActivateForFile($appUserModelId, $PdfPath)
    $deadline = (Get-Date).AddSeconds(15)
    $activated = $null
    do {
        Start-Sleep -Milliseconds 250
        $activated = Get-Process -Id $activatedProcessId -ErrorAction SilentlyContinue
    } while ($null -eq $activated -and (Get-Date) -lt $deadline)
    if ($null -eq $activated -or $activated.HasExited) { Fail 'Explicit PDF file-association activation did not keep ElliePdf alive.' }
    Stop-Process -Id $activatedProcessId -Force -ErrorAction Stop
}

# Resolve every target and inspect exact metadata before any guard can permit mutation.
$currentPath = Resolve-ExactFile $PackagePath 'Current package'
$previousPath = Resolve-ExactFile $PreviousPackagePath 'Older package'
$rollbackPath = Resolve-ExactFile $RollbackPackagePath 'Forward rollback package'
$rotationPath = Resolve-ExactFile $CertificateRotationPackagePath 'Certificate-rotation package'
$current = Assert-Package $currentPath 'Current package' $ExpectedIdentityName $ExpectedPublisher $ExpectedArchitecture
$previous = Assert-Package $previousPath 'Older package' $ExpectedIdentityName $ExpectedPublisher $ExpectedArchitecture
$rollback = Assert-Package $rollbackPath 'Forward rollback package' $ExpectedIdentityName $ExpectedPublisher $ExpectedArchitecture
$rotation = Assert-Package $rotationPath 'Certificate-rotation package' $ExpectedIdentityName $ExpectedPublisher $ExpectedArchitecture
$syntheticPdf = Join-Path ([IO.Path]::GetTempPath()) ('elliepdf-package-lifecycle-' + [guid]::NewGuid().ToString('N') + '.pdf')
if ($current.VersionText -ne $ExpectedVersion) { Fail "Current package version '$($current.VersionText)' does not equal '$ExpectedVersion'" }
if ($previous.Version -ge $current.Version) { Fail 'Older package must have a strictly lower version than the current package.' }
if ($rollback.Version -le $current.Version) { Fail 'Forward rollback package must have a strictly higher version than the current package.' }
if ($rotation.Version -le $rollback.Version) { Fail 'Certificate-rotation package must be newer than the rollback package.' }
$currentSignature = Get-AuthenticodeSignature -FilePath $currentPath
$rotationSignature = Get-AuthenticodeSignature -FilePath $rotationPath
if ([string]$rotationSignature.SignerCertificate.Subject -ne $ExpectedPublisher) { Fail 'Certificate-rotation signer subject does not match the package publisher.' }
if ([string]$rotationSignature.SignerCertificate.Thumbprint -ne $RotatedCertificateThumbprint) { Fail 'Certificate-rotation signer thumbprint does not match the explicitly supplied thumbprint.' }
if ([string]$rotationSignature.SignerCertificate.Thumbprint -eq [string]$currentSignature.SignerCertificate.Thumbprint) { Fail 'Certificate-rotation package did not use a new signing certificate.' }

if (-not $Execute) { Write-Host 'Safe mode: all package targets and metadata were resolved; no install, activation, upgrade, downgrade, rollback, uninstall, or cleanup action was executed.'; exit 0 }
if (-not $AllowDestructive) { Fail 'Package lifecycle execution changes VM install state; pass both -Execute and -AllowDestructive.' }
if ($env:ELLIEPDF_PACKAGE_TEST_VM -ne '1') { Fail 'Refusing execution: set ELLIEPDF_PACKAGE_TEST_VM=1 only inside the dedicated clean Windows VM.' }
if (-not $IsWindows) { Fail 'Package lifecycle execution is Windows-only.' }
if (@(Get-AppxPackage -Name $ExpectedIdentityName -ErrorAction SilentlyContinue).Count -ne 0) { Fail 'Refusing to mutate this machine: an ElliePdf package is already installed. Use a clean disposable VM.' }

try {
    [IO.File]::WriteAllBytes($syntheticPdf, [byte[]](0x25,0x50,0x44,0x46,0x2D,0x31,0x2E,0x37,0x0A,0x25,0xFF,0xFF,0xFF,0xFF,0x0A))
    Add-AppxPackage -Path $previousPath -ErrorAction Stop
    $installed = Assert-Installed $ExpectedIdentityName $ExpectedPublisher $previous.Version
    $stateRoot = Join-Path $env:LOCALAPPDATA ('Packages\' + $installed.PackageFamilyName + '\LocalState')
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    $stateMarker = Join-Path $stateRoot 'package-lifecycle-settings-recovery.marker'
    Set-Content -LiteralPath $stateMarker -Value 'preserve-me' -NoNewline
    Add-AppxPackage -Path $currentPath -ErrorAction Stop
    $installed = Assert-Installed $ExpectedIdentityName $ExpectedPublisher $current.Version
    Assert-FileActivation $installed $syntheticPdf
    Add-AppxPackage -Path $rollbackPath -ErrorAction Stop
    $installed = Assert-Installed $ExpectedIdentityName $ExpectedPublisher $rollback.Version
    if ((Get-Content -LiteralPath $stateMarker -Raw) -ne 'preserve-me') { Fail 'Settings/recovery marker was not preserved across forward rollback.' }
    Assert-FileActivation $installed $syntheticPdf
    Add-AppxPackage -Path $rotationPath -ErrorAction Stop
    Assert-Installed $ExpectedIdentityName $ExpectedPublisher $rotation.Version | Out-Null
    if ((Get-Content -LiteralPath $stateMarker -Raw) -ne 'preserve-me') { Fail 'Settings/recovery marker was not preserved across certificate rotation.' }
    try {
        Add-AppxPackage -Path $previousPath -ErrorAction Stop
        Fail 'Older package downgrade unexpectedly succeeded.'
    }
    catch {
        if ($_.Exception.Message -like '*unexpectedly succeeded*') { throw }
        $deploymentFailure = ($_ | Out-String)
        $exception = $_.Exception
        while ($null -ne $exception) {
            $deploymentFailure += "`n0x{0:X8}" -f ($exception.HResult -band 0xffffffffL)
            $exception = $exception.InnerException
        }
        if ($deploymentFailure -notmatch '(?i)0x80073D06') { throw }
        Assert-Installed $ExpectedIdentityName $ExpectedPublisher $rotation.Version | Out-Null
        Write-Host 'PASS older-package downgrade rejected with ERROR_INSTALL_PACKAGE_DOWNGRADE (0x80073D06).'
    }
    $installedForRemoval = Assert-Installed $ExpectedIdentityName $ExpectedPublisher $rotation.Version
    Remove-AppxPackage -Package $installedForRemoval.PackageFullName -ErrorAction Stop
    if (@(Get-AppxPackage -Name $ExpectedIdentityName -ErrorAction SilentlyContinue).Count -ne 0) { Fail 'Uninstall did not remove the exact package.' }
    Write-Host 'PASS package lifecycle: clean install, file association, upgrade, downgrade rejection, forward rollback, certificate rotation, settings/recovery preservation, uninstall.'
} finally {
    if (Test-Path -LiteralPath $syntheticPdf) { Remove-Item -LiteralPath $syntheticPdf -Force -ErrorAction SilentlyContinue }
    foreach ($remainingPackage in @(Get-AppxPackage -Name $ExpectedIdentityName -ErrorAction SilentlyContinue)) {
        Remove-AppxPackage -Package $remainingPackage.PackageFullName -ErrorAction SilentlyContinue
    }
}
