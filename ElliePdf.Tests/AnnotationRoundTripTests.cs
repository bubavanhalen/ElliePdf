using ElliePdf.Models;
using ElliePdf.Services;
using Xunit;

namespace ElliePdf.Tests;

/// <summary>
/// Annotations are now the only storage: no companion file, and a shared PDF must carry everything
/// while remaining editable when reopened.
/// </summary>
public sealed class AnnotationRoundTripTests : IDisposable
{
    private const string SourceText = "Hello searchable world";

    private readonly string _directory;

    public AnnotationRoundTripTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"elliepdf-rt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        PdfTestHarness.EnsureInitialized();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string At(string name) => Path.Combine(_directory, name);

    private static PageOverlayState SampleOverlay() => new()
    {
        InkStrokes =
        {
            new InkStrokeOverlay
            {
                ColorHex = "#FF0000",
                Thickness = 5,
                Points =
                {
                    new PointOverlay { X = 40, Y = 200, Pressure = 1.0 },
                    new PointOverlay { X = 180, Y = 200, Pressure = 0.4 },
                    new PointOverlay { X = 340, Y = 200, Pressure = 0.15 }
                }
            }
        },
        Shapes =
        {
            new ShapeOverlay
            {
                Kind = ShapeKind.Rectangle,
                Start = new PointOverlay { X = 60, Y = 60 },
                End = new PointOverlay { X = 200, Y = 130 },
                ColorHex = "#1A73E8",
                Thickness = 3
            }
        },
        TextItems =
        {
            new TextOverlay
            {
                X = 40,
                Y = 240,
                Width = 300,
                Height = 30,
                FontSize = 14,
                Text = "Annotated",
                ColorHex = "#008000"
            }
        }
    };

    /// <summary>Writes an overlay through the production save path.</summary>
    private string Embed(string sourcePath, PageOverlayState overlay, string outputName)
    {
        var overlays = new PageOverlayDocument();
        overlays.Pages[0] = overlay;

        var document = PdfTestHarness.Open(sourcePath);
        try
        {
            PdfOverlayWriter.WriteDocument(document, overlays, pageCount: 1);
            var outputPath = At(outputName);
            PdfTestHarness.Save(document, outputPath);
            return outputPath;
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }

    [Fact]
    public void Annotations_come_back_with_every_detail_intact()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var output = Embed(source, SampleOverlay(), "annotated.pdf");

        var document = PdfTestHarness.Open(output);
        try
        {
            var overlays = PdfAnnotationReader.ExtractOwnAnnotations(document, pageCount: 1);
            var page = overlays.Pages[0];

            var ink = Assert.Single(page.InkStrokes);
            Assert.Equal("#FF0000", ink.ColorHex);
            Assert.Equal(5, ink.Thickness);
            Assert.Equal(3, ink.Points.Count);

            // Pressure is the detail an appearance stream alone could never give back.
            Assert.Equal(1.0, ink.Points[0].Pressure, 3);
            Assert.Equal(0.4, ink.Points[1].Pressure, 3);
            Assert.Equal(0.15, ink.Points[2].Pressure, 3);

            var shape = Assert.Single(page.Shapes);
            Assert.Equal(ShapeKind.Rectangle, shape.Kind);
            Assert.Equal(60, shape.Start.X, 3);
            Assert.Equal(130, shape.End.Y, 3);
            Assert.Equal("#1A73E8", shape.ColorHex);

            var text = Assert.Single(page.TextItems);
            Assert.Equal("Annotated", text.Text);
            Assert.Equal("#008000", text.ColorHex);
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }

    [Fact]
    public void Reading_annotations_detaches_them_so_nothing_draws_twice()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var output = Embed(source, SampleOverlay(), "annotated.pdf");

        var document = PdfTestHarness.Open(output);
        try
        {
            var before = PdfTestHarness.AnnotationCount(document, 0);
            Assert.True(before >= 3, $"expected the annotations to be present, found {before}");

            PdfAnnotationReader.ExtractOwnAnnotations(document, pageCount: 1);

            Assert.Equal(0, PdfTestHarness.AnnotationCount(document, 0));
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }

    [Fact]
    public void Annotations_are_visible_to_any_viewer()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var output = Embed(source, SampleOverlay(), "annotated.pdf");

        var document = PdfTestHarness.Open(output);
        try
        {
            // Rendering with annotations on is what every other viewer does.
            var render = PdfTestHarness.Render(document, 0, renderAnnotations: true);

            var onStroke = PdfTestHarness.PixelAt(render, 100, 200);
            Assert.True(
                onStroke.R > 150 && onStroke.G < 110 && onStroke.B < 110,
                $"the ink annotation did not render: B={onStroke.B} G={onStroke.G} R={onStroke.R}");

            // And the page's own text layer is untouched.
            Assert.Contains("Hello searchable", PdfTestHarness.ExtractText(document, 0));
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }

    [Fact]
    public void Annotations_survive_an_edit_and_resave_cycle()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var first = Embed(source, SampleOverlay(), "pass1.pdf");

        // Reopen, pull the annotations back, add another, and save again.
        PageOverlayDocument overlays;
        var document = PdfTestHarness.Open(first);
        try
        {
            overlays = PdfAnnotationReader.ExtractOwnAnnotations(document, pageCount: 1);
            overlays.Pages[0].Shapes.Add(new ShapeOverlay
            {
                Kind = ShapeKind.Ellipse,
                Start = new PointOverlay { X = 220, Y = 60 },
                End = new PointOverlay { X = 340, Y = 130 },
                ColorHex = "#B3261E",
                Thickness = 2
            });

            PdfOverlayWriter.WriteDocument(document, overlays, pageCount: 1);
            PdfTestHarness.Save(document, At("pass2.pdf"));
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }

        var reopened = PdfTestHarness.Open(At("pass2.pdf"));
        try
        {
            var page = PdfAnnotationReader.ExtractOwnAnnotations(reopened, pageCount: 1).Pages[0];

            // Nothing duplicated, and the new shape is there alongside the originals.
            Assert.Single(page.InkStrokes);
            Assert.Single(page.TextItems);
            Assert.Equal(2, page.Shapes.Count);
            Assert.Contains(page.Shapes, shape => shape.Kind == ShapeKind.Ellipse);
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(reopened);
        }
    }

    [Fact]
    public void Annotations_from_other_tools_are_left_alone()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);

        // A foreign annotation with no ElliePdf payload.
        var document = PdfTestHarness.Open(source);
        try
        {
            var page = PdfiumNative.FPDF_LoadPage(document, 0);
            var foreign = PdfiumNative.FPDFPage_CreateAnnot(page, PdfiumNative.AnnotSquare);
            var rect = new FsRectF { left = 10, bottom = 10, right = 100, top = 60 };
            PdfiumNative.FPDFAnnot_SetRect(foreign, ref rect);
            PdfiumNative.FPDFPage_CloseAnnot(foreign);
            PdfiumNative.FPDF_ClosePage(page);
            PdfTestHarness.Save(document, At("foreign.pdf"));
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }

        var reopened = PdfTestHarness.Open(At("foreign.pdf"));
        try
        {
            var overlays = PdfAnnotationReader.ExtractOwnAnnotations(reopened, pageCount: 1);

            Assert.Empty(overlays.Pages);
            Assert.Equal(1, PdfTestHarness.AnnotationCount(reopened, 0));
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(reopened);
        }
    }
}
