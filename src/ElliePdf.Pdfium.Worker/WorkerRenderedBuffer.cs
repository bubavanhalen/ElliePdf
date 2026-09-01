using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Pdfium.Worker;

public sealed record WorkerRenderedBuffer(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    PixelFormat Format,
    RenderKey Key,
    RenderGeneration Generation);
