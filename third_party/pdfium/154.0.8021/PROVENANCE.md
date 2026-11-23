# PDFium 154.0.8021 provenance

ElliePdf consumes the exact NuGet package `bblanchon.PDFium.Win32` version
`154.0.8021`. The package is published by Benoît Blanchon and identifies its
source repository as `https://github.com/bblanchon/pdfium-binaries.git`, commit
`9dd99a8991bed3a2f37658a31bcd5b403800fd03`. NuGet package SHA-256:
`B5B3C4E567CDE273E745CCE45EBD8145DF08D49C2E1506F51F20822DEB06DCC7`.

The native assets are verified by `eng/Verify-PdfiumNative.ps1` before release:

| RID | SHA-256 | Length | PE machine |
|---|---|---:|---|
| win-x64 | `2A9031FA88F412147C3BC7115054550048C724DB6EA70298B6C6B0D13E513882` | 7,262,720 | `0x8664` |
| win-arm64 | `B8A41647AC18C039C4A9CE4F00C1D71A08133EDF92531A9C7903FD985A04DB73` | 6,705,152 | `0xAA64` |

The verifier also parses the PE export directory and requires every symbol used
by the managed PDFium boundary. No DLL is copied between architectures.
