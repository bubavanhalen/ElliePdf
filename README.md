# ElliePdf

A Windows native PDF reader and organizer built with **WinUI 3** and **PDFium**.

## Features

- **Read** — Multi-tab PDF viewing with adaptive navigation, direct-pixel rendering, text search, links, forms, and outlines
- **Isolated engine** — PDFium runs in a bounded worker process; the WinUI process never loads the parser
- **Labs** — Organizer and annotation workflows stay opt-in until their post-Stable integrity gates pass
- **Password-protected PDFs** — Prompts for a password when opening encrypted files
- **File association** — Double-click a `.pdf` to open in ElliePdf

## Design

The UI follows Fluent 2 with an Ellie-branded `#dcae96` accent system (`Themes/ElliePdf.xaml`):

- Custom title bar with integrated document tabs, a Read/Organize workspace switcher, and settings
- Chromeless reader with floating pill toolbars that auto-hide while reading
- Floating Pages/Outline/Search panels with slide-in animations and shadows
- Drop-zone empty state with rich recent-file cards and drag & drop support
- Card-based, instant-apply Settings page with theme picker

## Requirements

- Windows 11, build 26100 or later
- The exact .NET SDK pinned in `global.json` (11.0.100-preview.7.26381.103)
- The pinned `bblanchon.PDFium.Win32` `154.0.8021` package; the build creates an
  architecture-matched private worker bundle automatically

## Build and run

```powershell
dotnet build -p:Platform=x64
```

Restore is lock-file based. CI and Release builds require the committed `packages.lock.json` files and do not update them implicitly.

The supported release architectures are `win-x64` and `win-arm64`. Each RID has
its own verified PDFium asset; run `./eng/Verify-PdfiumNative.ps1` after restore
to verify hashes, PE architecture, and required exports. No cross-architecture
fallback is permitted.

The worker executable and its verified native dependency are copied into the app-private
`PdfWorker` output directory. Do not copy `pdfium.dll` beside the UI executable. Launch from
Visual Studio using **ElliePdf (Package)** or **ElliePdf (Unpackaged)**.

## Branding

App icons use a playful folded-page monogram mark in `#dcae96` (named for Ellie) on a transparent background. Source artwork lives in `Assets/Brand/elliepdf-logo-master.png`.

## Project layout

```
ElliePdf/
├── ElliePdf.Core/   Shared non-UI logic (zoom calculations)
├── Pages/           ReaderPage and OrganizePage
├── ViewModels/      MVVM view models
├── Services/        WinUI compatibility facade and document session
├── src/             Domain, application, transport, rendering, client and isolated worker
├── Controls/        Reusable UI controls (PdfPageViewer)
├── Navigation/      Cross-page navigation helpers
└── Assets/          App icons and tiles
```

## Architecture

- `IPdfService` / `PdfService` — UI facade over the authenticated worker client
- `ElliePdf.Pdfium.Worker` — the only production process that loads PDFium
- `IDocumentSessionService` — Shared active document for Reader and Organize
- `ReaderViewModel` — Page rendering, zoom, search, and edit mode
- `DocumentCollectionViewModel` — Multi-document organize workspace

## Icons

Regenerate transparent PNG tiles and multi-size `AppIcon.ico` from the brand master artwork (`pip install pillow` once):

```powershell
.\tools\Generate-AppIcons.ps1
```

## Tests

```powershell
dotnet test ElliePdf.Tests\ElliePdf.Tests.csproj
```

## Publish

Microsoft Store is the GA distribution vehicle. Release builds are self-contained and enable Native AOT in Release configuration; PDFium compatibility must be verified by the publish pipeline. Produce the architecture-specific publish trees and unsigned packages with `eng/Publish-ReleaseArtifacts.ps1` (`-Platform x64 -RuntimeIdentifier win-x64 -Package`, then `-Platform ARM64 -RuntimeIdentifier win-arm64 -Package`). Store packaging must be validated on both supported architectures. See [SUPPORTED_WINDOWS.md](SUPPORTED_WINDOWS.md) and `third_party/pdfium/154.0.8021/PROVENANCE.md` for the native supply record.
