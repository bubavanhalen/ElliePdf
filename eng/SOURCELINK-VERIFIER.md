# SourceLink verifier

`Test-SourceLink.ps1` runs an offline gate over Release symbols. It reads portable
PDB metadata with the .NET platform APIs and requires a non-empty SourceLink
`documents` mapping containing HTTPS URLs without credentials or local endpoints.
Document keys must use the deterministic `/_/` path root, so published PDBs do
not disclose a developer or runner checkout path. At least one managed portable
PDB must pass; a directory containing only skipped native/AOT PDBs fails closed.
It performs no network access. Non-portable PDBs are reported as `SKIP` when they
belong to native/AOT output; a non-portable PDB next to a managed assembly fails
closed because its SourceLink data cannot be verified.

Run `pwsh -File eng/Test-SourceLink.ps1` after producing the Release symbols.
The verifier's built-in self-test can be run with `dotnet run --project
eng/SourceLinkVerifier -- --self-test`.
