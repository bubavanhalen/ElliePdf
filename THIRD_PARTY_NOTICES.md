# Third-party notices

ElliePdf redistributes the following native dependency:

| Component | Version | License | Source | Integrity |
|---|---|---|---|---|
| `bblanchon.PDFium.Win32` | `154.0.8021` | Apache-2.0 | [upstream repository](https://github.com/bblanchon/pdfium-binaries/tree/9dd99a8991bed3a2f37658a31bcd5b403800fd03) | See [`PROVENANCE.md`](third_party/pdfium/154.0.8021/PROVENANCE.md) |

The package declares Apache-2.0 in its NuGet metadata. The complete license text
is retained in [`third_party/pdfium/154.0.8021/LICENSE.txt`](third_party/pdfium/154.0.8021/LICENSE.txt),
and the package attribution, source commit, package hash, native hashes and
export requirements are recorded in the adjacent provenance and SBOM files.

Do not ship a native payload unless `eng/Verify-PdfiumNative.ps1` and
`eng/Test-ReleaseEvidence.ps1` pass for every advertised architecture.
