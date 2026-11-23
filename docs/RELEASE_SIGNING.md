# Protected release signing

This repository never stores the production package publisher, the signing
certificate, or Store submission credentials. The checked-in
`Package.appxmanifest` stays on the local-only development identity
`CN=ElliePdf Development`. Production identity injection happens only inside the
protected release lane.

## Required protected environment

- A protected GitHub Actions environment named `release-signing`.
- Environment deployment rules that allow the trusted `master` branch and
  signed release tags only; dispatching a privileged workflow from another ref
  is forbidden.
- A self-hosted Windows x64 runner with the `elliepdf-signing` label.
- An ephemeral signing runner image, asserted with
  `ELLIEPDF_SIGNING_EPHEMERAL_RUNNER=1`, whose certificate/key access disappears
  when the job ends.
- The exact newest validated .NET 11 preview SDK pinned by `global.json` and the current locked packages.
- Windows SDK signing tools with `signtool.exe`.
- Windows App Certification Kit with `appcert.exe`.
- The production package publisher exposed as `ELLIEPDF_PRODUCTION_PUBLISHER`.
- The exact reserved Store/package identity name exposed as
  `ELLIEPDF_PRODUCTION_IDENTITY_NAME`.
- The installed signing certificate thumbprint exposed as
  `ELLIEPDF_SIGNING_CERT_THUMBPRINT`.
- An installed certificate in `CurrentUser\My` or `LocalMachine\My` whose
  subject exactly matches the injected publisher, has an accessible private key,
  and includes the code-signing EKU `1.3.6.1.5.5.7.3.3`.
- Optional RFC 3161 timestamp endpoint as `ELLIEPDF_TIMESTAMP_URL`.

## Workflow shape

`/.github/workflows/release-signing.yml` checks out an explicit `vM.m.p[-pre]`
tag on the protected runner and runs `eng/Invoke-ProtectedRelease.ps1`.

That script:

1. Enforces `GITHUB_ACTIONS=true`, the trusted `${{ runner.environment }}` context as `self-hosted`,
   `ELLIEPDF_RELEASE_SIGNING=1`, `ELLIEPDF_RELEASE_ENVIRONMENT=release-signing`,
   and an exact tag checkout.
2. Restores `ElliePdf.slnx` with `--locked-mode`.
3. Verifies pinned PDFium native payloads, SBOM generation, release evidence,
   toolchain fingerprinting, and packaging contract tests.
4. Backs up `Package.appxmanifest`, injects the exact production publisher for
   the controlled build, verifies the reserved package identity name, and
   restores the development manifest afterward.
5. Builds NativeAOT x64 and ARM64 packages from the exact tag and preserves the
   unsigned MSIX inputs.
6. Signs a copied package with SHA-256 via `signtool.exe`, verifies the package
   signature, reruns static payload validation, records unsigned and signed
   SHA-256 hashes with provenance, and creates a detached CMS signature over the
   checksum record.
7. Runs WACK against the signed package for each architecture.

## Outputs

`artifacts/release-candidate/<tag>/` contains:

- `sbom.json`
- `toolchain-fingerprint.json`
- `Package.appxmanifest.original`
- `win-x64/unsigned/*`
- `win-x64/signed/*`
- `win-x64/records/*.checksums.json`
- `win-x64/records/*.checksums.json.p7s`
- `win-arm64/unsigned/*`
- `win-arm64/signed/*`
- `win-arm64/records/*.checksums.json`
- `win-arm64/records/*.checksums.json.p7s`
- `wack-report.xml` beside each signed package

## Store flighting

Store submission is deliberately a separate manual approval gate. The
`store-production` environment must have required reviewers, the
`elliepdf-store` self-hosted Windows runner, and the protected variables
`ELLIEPDF_STORE_APPROVED=1`, `ELLIEPDF_STORE_EPHEMERAL_RUNNER=1`,
`ELLIEPDF_STORE_IDENTITY_NAME`, `ELLIEPDF_STORE_PUBLISHER`, the protected
`ELLIEPDF_STORE_PRODUCT_ID`, and a comma-separated
`ELLIEPDF_ALLOWED_FLIGHT_IDS`. Install a non-exportable Entra application
authentication certificate on the ephemeral runner and expose its exact
thumbprint as `ELLIEPDF_STORE_AUTH_CERT_THUMBPRINT`. Configure the following
identifiers as Actions secrets only: `AZURE_AD_TENANT_ID`, `SELLER_ID`, and
`AZURE_AD_APPLICATION_CLIENT_ID`. Client-secret authentication is deliberately
not accepted because the Store CLI would receive that secret on its process
command line.
Its deployment-branch rule must allow only `master`; both privileged workflows
also verify `GITHUB_WORKFLOW_REF`. All actions in signing, Store, and lifecycle
lanes are pinned to immutable reviewed commits.

Run `.github/workflows/store-flighting.yml` with the exact successful signing
run ID, tag, target (`flight` or `stable`), optional flight ID, and
one explicit operation: `status`, `submit`, `rollout`, `halt`, or `finalize`.
`submit` uploads only the two verified signed packages into a draft, enables the
chosen rollout percentage, then explicitly publishes and polls it. Flight and
stable operations select the corresponding official `msstore` command family.
Every operation verifies the signing run, source commit, package identities,
trusted Appx signatures, signed hashes, and detached CMS records before invoking
`msstore`. Store halt is an operational stop, not a rollback for users who
already received a package; a corrective release must be submitted forward.
The workflow executes the current reviewed automation from the exact protected
`master` workflow commit; selected release tags are resolved and authenticated
as data and are never used as the source of scripts that receive Store access.

The workflow uses Microsoft's `microsoft/microsoft-store-apppublisher@v1.4`
action and pins Store CLI `v0.4.1`, whose multi-package input-directory contract
is verified at runtime. Credentials are reset in a `finally` block and the
runner must be ephemeral. The app must already be live and have an
Entra application associated with Partner Center with the Manager role before
this lane can be enabled.

## Explicit non-goals

- No production publisher, certificate material, or Partner Center credentials
  are committed here.
- No Store credentials or production Store identity are committed here. The
  flighting workflow is intentionally not triggered by a tag or signing
  completion; a human must dispatch and approve it.
