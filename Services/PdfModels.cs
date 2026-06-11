namespace ElliePdf.Services;

public sealed record RenderedPage(
    byte[] PngBytes,
    int Width,
    int Height,
    float PageWidthPoints,
    float PageHeightPoints);

public sealed record TextMatch(
    int PageIndex,
    int CharIndex,
    int MatchLength,
    string Context);
