# Benchmark corpus

This directory commits the manifest and deterministic generator only. Generated PDFs stay ignored under `testdata/generated/`; no downloaded or proprietary PDF is used.

On Windows, use the same pinned Python 3.13 dependencies and manifest checks as CI:

```powershell
./eng/Initialize-TestCorpus.ps1
```

`synthetic-activation-scan.pdf` is a five-page, 14,755,273-byte raster fixture for the cold activation gate.

The 1-GB stress fixture is deliberately opt-in and is never committed:

```powershell
./eng/Initialize-TestCorpus.ps1 -Include1Gb
```

It is a valid one-page synthetic PDF padded deterministically to exactly 1,073,741,824 bytes. Its pinned SHA-256 is
`5A54A8E2725B76FF10D82623A25F6745105321B8DEB509A562FC28811C01ECF7`; the benchmark harness verifies both size and
hash before use. The generated fixture stays outside source control.

`manifest.json` pins each generated SHA-256. The encrypted fixture uses the non-secret test-only password `ellie-test`. `synthetic-corrupt.pdf` is intentionally truncated and must fail to open. The recipes use ReportLab's invariant mode and were verified byte-for-byte deterministic across repeated generation.
