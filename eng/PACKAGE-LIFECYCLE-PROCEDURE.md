# Signed package lifecycle procedure

`Invoke-PackageSmoke.ps1` is an intentionally destructive, fail-closed release
gate. Run it only in a disposable, interactive Windows VM that has never had
ElliePdf installed. It resolves and statically validates all four signed MSIX
targets before changing package state.

Execution requires all of the following independent operator intent and VM
guards:

```powershell
$env:ELLIEPDF_PACKAGE_TEST_VM = '1'
.\eng\Invoke-PackageSmoke.ps1 `
  -PackagePath .\artifacts\current\ElliePdf.msix `
  -PreviousPackagePath .\artifacts\previous\ElliePdf.msix `
  -RollbackPackagePath .\artifacts\rollback\ElliePdf.msix `
  -CertificateRotationPackagePath .\artifacts\rotation\ElliePdf.msix `
  -ExpectedIdentityName 'reserved-package-identity' `
  -ExpectedPublisher 'CN=reserved-publisher' `
  -ExpectedArchitecture x64 `
  -ExpectedVersion 1.2.3.4 `
  -RotatedCertificateThumbprint '0123456789ABCDEF0123456789ABCDEF01234567' `
  -Execute -AllowDestructive
```

Omit `-Execute` for safe mode. Safe mode resolves every exact path, checks
identity, publisher, architecture, version ordering, and signatures, then
exits without installing, activating, or removing anything. `-Execute` without
`-AllowDestructive`, a missing `ELLIEPDF_PACKAGE_TEST_VM=1`, a non-Windows host,
or an already-installed ElliePdf package fails before mutation.

The matrix is: clean install of the older signed package; upgrade to the
current package; launch a generated synthetic PDF through the registered `.pdf`
association using Windows application activation; install the forward-versioned
last-known-good rollback package; verify the settings/recovery marker through
each update; install the still-newer certificate-rotation package and verify its
new signing thumbprint;
verify that direct installation of the older package is rejected; and uninstall
the exact package. A `finally` block removes the synthetic fixture and any
remaining package after a failure.

The offline network check remains an external packet-capture/firewall gate. Use
`eng/OFFLINE-NETWORK-PROCEDURE.md` on the same clean VM and attach its capture
summary to the release evidence; this script does not pretend to replace that
observation.

This procedure still requires real operator-signed packages, a trusted rotated
certificate, an interactive clean Windows VM, and an operator to review the
activation and packet-capture evidence.

The protected `.github/workflows/package-lifecycle.yml` resolves and
authenticates all four successful Release Signing artifacts before invoking this
script. The workflow checks out the exact protected `master` workflow commit,
so package tags are authenticated inputs and can never replace the lifecycle
harness that receives destructive VM authority. Run it once on an x64 VM and
once on a physical or virtual ARM64 test host; its environment approval is
independent from release signing and Store submission.
