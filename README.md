# ElliePdf

A Windows native PDF reader and organizer built with **WinUI 3** and **PDFium**.

## Features

- **Read** — Multi-tab PDF viewing with zoom, page navigation, text search, and in-place editing
- **Organize** — Reorder, rotate, and delete pages; export merged PDFs in grid order
- **Edit** — Ink, text, and signatures on the active page; **Save** (with overwrite confirmation) and **Save As**
- **Personalize** — System/Light/Dark theme and instant-apply settings
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

- Windows 10 1809 or later
- Visual Studio 2022 with the Windows App SDK workload, or .NET SDK 11+
- Native `pdfium.dll` (copied automatically from the `PDFium.WindowsV2` NuGet package on build)

## Build and run

```powershell
dotnet build -p:Platform=x64
```

Launch from Visual Studio using **ElliePdf (Package)** or **ElliePdf (Unpackaged)**.

## Branding

App icons use a playful folded-page monogram mark in `#dcae96` (named for Ellie) on a transparent background. Source artwork lives in `Assets/Brand/elliepdf-logo-master.png`.

## Project layout

```
ElliePdf/
├── ElliePdf.Core/   Shared non-UI logic (zoom calculations)
├── Pages/           ReaderPage and OrganizePage
├── ViewModels/      MVVM view models
├── Services/        PDFium wrapper and document session
├── Controls/        Reusable UI controls (PdfPageViewer)
├── Navigation/      Cross-page navigation helpers
└── Assets/          App icons and tiles
```

## Architecture

- `IPdfService` / `PdfService` — PDFium P/Invoke for open, render, search, merge, save
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

Release builds are self-contained (Native AOT disabled for PDFium compatibility). Use the publish profiles under `Properties/PublishProfiles/`.
