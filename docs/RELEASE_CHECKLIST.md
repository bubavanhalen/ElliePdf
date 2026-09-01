# ElliePdf release checklist

This checklist is intentionally usable for unsigned CI artifacts. Signing and
Store submission are separate approval gates and must never be simulated.

1. Confirm the exact preview SDK in `global.json`; restore with `--locked-mode`.
2. Run `eng/Verify-PdfiumNative.ps1` for `win-x64` and `win-arm64`.
3. Run `eng/Generate-Sbom.ps1` and `eng/Test-ReleaseEvidence.ps1`.
4. Build and test both architectures, then publish unsigned MSIX payloads.
   Derive the four-part package version with `eng/Get-MsixVersion.ps1`; tags
   `vM.m.p[-pre]` map to `M.m.p.build`. A rollback rebuild uses the preserved
   last-known-good payload with a strictly greater build component.
5. Record payload hashes, toolchain fingerprint, SBOM, provenance, notices and
   test results together. Preserve the unsigned payload as the rollback input.
6. Run the minimum Windows build and current Windows GA smoke matrix. Insider
   results are compatibility evidence, not a release substitute.
7. Obtain independent signing approval, verify signed-package hashes against the
   recorded unsigned payload, verify the exact reserved package identity, then replace the manifest's explicitly local-only
   `CN=ElliePdf Development` identity with the reserved Partner Center identity
   during the controlled release build. The protected lane is documented in
   `docs/RELEASE_SIGNING.md` and implemented by
   `eng/Invoke-ProtectedRelease.ps1`, `eng/Set-ManifestPublisher.ps1`, and
   `eng/Sign-ReleasePackage.ps1`.
8. Store flighting remains a separate, manually dispatched approval gate. The
   protected `store-production` environment, `elliepdf-store` runner, and
   `eng/Invoke-StoreFlight.ps1` enforce signed-artifact verification, explicit
   operation selection, a non-exportable certificate credential on the
   ephemeral runner, and Partner Center identifiers sourced only from Actions
   secrets. Use `.github/workflows/store-flighting.yml`; never add
   Store credentials or an automatic publish trigger to this repository.
9. Run `.github/workflows/package-lifecycle.yml` separately for x64 and ARM64
   on clean interactive disposable VMs. It authenticates four successful
   signing artifacts and executes install, upgrade, downgrade rejection,
   forward rollback, certificate rotation, explicit PDF activation, settings
   preservation, and uninstall. Attach the separate offline-network procedure.

The checked-in publisher is a development identity, not a production or Store
identity. Release automation must receive the real publisher and signing material
from the protected release environment; those values must never be guessed or
committed to this repository.

Release is blocked by any missing architecture, lock drift, native hash/export
failure, test failure, unsupported Windows minimum, or unexplained payload
hash difference.
