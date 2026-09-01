# Benchmark corpus

This directory commits the manifest and deterministic generator only. Generated PDFs stay ignored under `testdata/generated/`; no downloaded or proprietary PDF is used.

Generate the ten normal fixtures with the bundled workspace Python runtime (or any Python environment containing ReportLab and Pillow):

```powershell
python eng/Generate-TestCorpus.py testdata/generated
```

The 1-GB stress fixture is deliberately opt-in and is never committed:

```powershell
python eng/Generate-TestCorpus.py testdata/generated --include-1gb
```

It is a valid one-page synthetic PDF padded deterministically to exactly 1,073,741,824 bytes. Its pinned SHA-256 is
`5A54A8E2725B76FF10D82623A25F6745105321B8DEB509A562FC28811C01ECF7`; the benchmark harness verifies both size and
hash before use. The generated fixture stays outside source control.

`manifest.json` pins each generated SHA-256. The encrypted fixture uses the non-secret test-only password `ellie-test`. `synthetic-corrupt.pdf` is intentionally truncated and must fail to open. The recipes use ReportLab's invariant mode and were verified byte-for-byte deterministic across repeated generation.
