# ADR 0002: Store GA distribution and ARM64 asset policy

## Status

Accepted

## Decision

Microsoft Store is the GA distribution vehicle. Internal signed canaries may be
side-loaded, but AppInstaller is post-v1. x64 and ARM64 are first-class release
architectures and may only be advertised after the complete signed matrix passes.

The exact `bblanchon.PDFium.Win32` `154.0.8021` package supplies dedicated
win-x64 and win-arm64 native assets. `eng/Verify-PdfiumNative.ps1` verifies their
SHA-256, file length, PE machine (`0x8664`/`0xAA64`), and managed-boundary export
set. Release promotion requires that check for both RIDs. Cross-architecture
copying and unverified DLL substitution remain prohibited.
