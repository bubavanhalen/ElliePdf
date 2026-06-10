This folder should contain the native PDFium binary for win-x64 named pdfium.dll.

Place a compatible pdfium.dll here to enable native PDF rendering and publishing. The project csproj includes this file as Content/IncludeInPublish so it will be copied to build and publish outputs.

Obtain a PDFium build for Windows (win-x64) from a trusted source and place pdfium.dll here.

NOTE: Do not commit native binaries with unknown provenance to public repositories.
