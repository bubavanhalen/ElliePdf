# Supported Windows platforms

ElliePdf production v1 targets Windows 11, build **26100** or later. This is the
minimum declared in the .NET target, MSIX manifest, and build validation. Windows
10 and earlier Windows 11 builds are not supported release targets.

Supported release architectures:

- `win-x64`
- `win-arm64`

Microsoft Store is the GA distribution vehicle. Signed MSIX canaries may be
side-loaded internally. Store packages must be tested on both architectures and
the minimum supported Windows build before promotion.

## PDFium native assets

Both release architectures have dedicated, provenance-traceable assets from the
exact `bblanchon.PDFium.Win32` `154.0.8021` package. The package, PE machine,
length, SHA-256, and required export table are checked by
`eng/Verify-PdfiumNative.ps1`; an x64 DLL is never substituted for ARM64. See
`third_party/pdfium/154.0.8021/PROVENANCE.md` for the recorded evidence.
