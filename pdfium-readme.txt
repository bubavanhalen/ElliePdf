PDFium native binary required

Place the PDFium native binary (pdfium.dll for win-x64) into the output folder before running the app, for example:

  ElliePdf\runtimes\win-x64\native\pdfium.dll

When publishing with Native AOT, ensure the native library is included in the publish output. The easiest approach is to place the native dll under runtimes\win-x64\native and ensure the csproj includes native assets. If you need help obtaining a PDFium build, download a compatible build for your platform and copy the dll into the path above.
