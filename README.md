# ElliePdf

A Windows native PDF reader and organizer built with **WinUI 3** and **PDFium**.

## Features

- **Read** — Multi-tab PDF viewing with adaptive navigation, direct-pixel rendering, text search, links, forms, and outlines
- **Isolated engine** — PDFium runs in a bounded worker process; the WinUI process never loads the parser
- **Labs** — Organizer and annotation workflows stay opt-in until their post-Stable integrity gates pass
- **Password-protected PDFs** — Prompts for a password when opening encrypted files
- **File association** — Double-click a `.pdf` to open in ElliePdf

## Requirements

- Windows 11, build 26100 or later
- The exact .NET SDK pinned in `global.json` (11.0.100-preview.7.26381.103)
- The pinned `bblanchon.PDFium.Win32` `154.0.8021` package; the build creates an
  architecture-matched private worker bundle automatically

## Build and run

### Visual Studio local debugging

Install Visual Studio Insiders compatible with the SDK in `global.json`, with
the WinUI application development tools, Windows SDK 26100 or newer, and the
pinned .NET preview SDK. Enable Windows Developer Mode for local package
registration. This repository intentionally stays on preview/experimental
dependencies; do not retarget it to a stable .NET version.

1. Open `ElliePdf.slnx` and wait for solution loading and NuGet restore to finish.
2. In Solution Explorer, right-click **ElliePdf** (not Benchmark or the worker)
   and choose **Set as Startup Project**.
3. Choose **Debug**, **x64**, and **ElliePdf (Package)**. If the configuration
   controls are hidden, use **Build > Configuration Manager**.
4. Press **F5** to build, register the development package, and debug. Use
   **Ctrl+F5** to launch without the debugger.

The build supplies the private worker, its runtime, and architecture-matched
PDFium automatically. Local Debug deployment does not require release signing
credentials or a prior publish. The unsupported Unpackaged profile has been
removed. ARM64 builds are supported, but require ARM64 hardware for native
execution testing.

### Command-line build

```powershell
dotnet build ElliePdf.slnx -c Debug -p:Platform=x64
```

Routine restore is locked in the IDE, Debug, Release, and CI; committed
`packages.lock.json` files must remain unchanged. For an intentional dependency
update, edit the central pins and explicitly use
`dotnet restore ElliePdf.slnx -p:RestoreLockedMode=false --force-evaluate`, then
review and commit the resulting lock-file changes.

The supported release architectures are `win-x64` and `win-arm64`. Each RID has
its own verified PDFium asset; run `./eng/Verify-PdfiumNative.ps1` after restore
to verify hashes, PE architecture, and required exports. No cross-architecture
fallback is permitted.

The worker executable and its verified native dependency are copied into the app-private
`PdfWorker` output directory and development deployment recipe. Do not copy
`pdfium.dll` beside the UI executable.

### Troubleshooting

- Wait for initial IDE restore before building a fresh checkout. `devenv /Build`
  can race background restore; it is not a replacement for `dotnet restore` in
  unattended build scripts.
- If a project is skipped or configuration mappings are unknown after pulling
  project changes, reload the solution and confirm Debug/x64 in Configuration
  Manager. Do not substitute AnyCPU for the app or worker.
- If a launch activates another ElliePdf window, close that instance first:
  the app redirects launches to its existing instance.
- If restore reports a lock mismatch, check `global.json` and the central pins;
  do not delete lock files or disable locked mode just to launch the app.
- To check the actual Debug deployment recipe, run
  `eng/Test-DevelopmentPayload.ps1 -RecipePath <path-to-ElliePdf.build.appxrecipe> -ExpectedArchitecture x64`.

See [the local readiness plan](docs/LOCAL_DEVELOPMENT_PLAN.md) for the acceptance
checks. Local testability is distinct from the production release gates in
[EXECUTION_SPEC.md](EXECUTION_SPEC.md).

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
