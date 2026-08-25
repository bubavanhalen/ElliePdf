using ElliePdf.Helpers;
using ElliePdf.Models;
using ElliePdf.Services;
using Xunit;

namespace ElliePdf.Tests;

/// <summary>
/// Covers the behaviour that used to be broken: annotating a page destroyed its text layer, because
/// the whole page was replaced with a flattened bitmap.
/// </summary>
public sealed class PdfOverlayWriterTests : IDisposable
{
    private const string SourceText = "Hello searchable world";
    private const float PageWidth = 400;
    private const float PageHeight = 300;

    private readonly string _directory;

    public PdfOverlayWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"elliepdf-tests-{Guid.NewGuid():N}");
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

    private static PageOverlayState InkOverlay(string colorHex = "#FF0000") => new()
    {
        InkStrokes =
        {
            new InkStrokeOverlay
            {
                ColorHex = colorHex,
                Thickness = 4,
                Points =
                {
                    new PointOverlay { X = 40, Y = 200 },
                    new PointOverlay { X = 360, Y = 200 }
                }
            }
        }
    };

    /// <summary>
    /// Runs the production embed path — the same call <c>PdfService.SaveDocumentWithOverlaysAsync</c>
    /// makes — so reverting to whole-page rasterization would fail these tests.
    /// </summary>
    private string Embed(string sourcePath, PageOverlayState overlay, string outputName, int pageCount = 1)
    {
        var overlays = new PageOverlayDocument();
        overlays.Pages[0] = overlay;

        var document = PdfTestHarness.Open(sourcePath);
        try
        {
            PdfOverlayWriter.WriteDocument(document, overlays, pageCount);

            var outputPath = At(outputName);
            PdfTestHarness.Save(document, outputPath);
            return outputPath;
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }

    private T Inspect<T>(string path, Func<IntPtr, T> inspect)
    {
        var document = PdfTestHarness.Open(path);
        try
        {
            return inspect(document);
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }

    [Fact]
    public void Ink_overlay_preserves_the_page_text_layer()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var output = Embed(source, InkOverlay(), "inked.pdf");

        Assert.Contains("Hello searchable", Inspect(output, d => PdfTestHarness.ExtractText(d, 0)));
    }

    [Fact]
    public void Ink_overlay_is_drawn_at_the_right_place_in_its_own_colour()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var output = Embed(source, InkOverlay(), "inked.pdf");

        Inspect(output, document =>
        {
            var render = PdfTestHarness.Render(document, 0);

            // The stroke spans x 40..360 at y 200 in display space.
            var onStroke = PdfTestHarness.PixelAt(render, 200, 200);
            Assert.True(onStroke.R > 180 && onStroke.G < 90 && onStroke.B < 90,
                $"expected red stroke at (200,200), got B={onStroke.B} G={onStroke.G} R={onStroke.R}");

            // Well clear of the stroke, and clear of the source text at the top.
            var offStroke = PdfTestHarness.PixelAt(render, 200, 260);
            Assert.True(offStroke.R > 240 && offStroke.G > 240 && offStroke.B > 240,
                $"expected blank paper at (200,260), got B={offStroke.B} G={offStroke.G} R={offStroke.R}");

            return true;
        });
    }

    [Fact]
    public void Text_overlay_is_written_as_real_selectable_text()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var overlay = new PageOverlayState
        {
            TextItems =
            {
                new TextOverlay
                {
                    X = 30,
                    Y = 120,
                    Width = 320,
                    Height = 40,
                    FontSize = 16,
                    Text = "Annotated",
                    ColorHex = "#0000FF"
                }
            }
        };

        var extracted = Inspect(Embed(source, overlay, "texted.pdf"), d => PdfTestHarness.ExtractText(d, 0));
        Assert.Contains("Annotated", extracted);
        Assert.Contains("Hello searchable", extracted);
    }

    [Fact]
    public void Text_overlay_lands_where_it_was_placed()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var overlay = new PageOverlayState
        {
            TextItems =
            {
                new TextOverlay
                {
                    X = 100,
                    Y = 150,
                    Width = 250,
                    Height = 30,
                    FontSize = 20,
                    Text = "Placed"
                }
            }
        };

        Inspect(Embed(source, overlay, "placed.pdf"), document =>
        {
            var box = PdfTestHarness.CharBoxOf(document, 0, "Placed");

            // PDF space is Y-up; display Y 150 is PDF Y 150 from the top of a 300pt page.
            Assert.InRange(box.Left, 95, 115);
            Assert.InRange(PageHeight - box.Top, 145, 175);
            return true;
        });
    }

    [Fact]
    public void Text_overlay_wraps_onto_multiple_lines_inside_its_box()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var overlay = new PageOverlayState
        {
            TextItems =
            {
                new TextOverlay
                {
                    X = 20,
                    Y = 100,
                    Width = 90,
                    Height = 120,
                    FontSize = 12,
                    Text = "wrapping across several lines of output"
                }
            }
        };

        var extracted = Inspect(Embed(source, overlay, "wrapped.pdf"), d => PdfTestHarness.ExtractText(d, 0));
        foreach (var word in new[] { "wrapping", "across", "several", "lines", "output" })
        {
            Assert.Contains(word, extracted);
        }
    }

    [Fact]
    public void Text_overlay_keeps_characters_outside_latin1()
    {
        // Stock PDF base-14 fonts are single-byte encoded and would silently drop all of these.
        const string unicode = "Привет 你好 — “quoted”";

        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var overlay = new PageOverlayState
        {
            TextItems =
            {
                new TextOverlay
                {
                    X = 20,
                    Y = 120,
                    Width = 360,
                    Height = 40,
                    FontSize = 14,
                    Text = unicode
                }
            }
        };

        var extracted = Inspect(Embed(source, overlay, "unicode.pdf"), d => PdfTestHarness.ExtractText(d, 0));
        Assert.Contains("Привет", extracted);
        Assert.Contains("你好", extracted);
    }

    [Fact]
    public void Signature_overlay_is_stamped_onto_the_page()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);

        IReadOnlyList<IReadOnlyList<StrokePoint>> strokes =
        [
            [new StrokePoint(0, 0), new StrokePoint(60, 0), new StrokePoint(60, 30)]
        ];

        Assert.True(SignatureRenderer.TryRender(strokes, out var png, out _));

        var overlay = new PageOverlayState
        {
            Signatures =
            {
                new SignatureOverlay
                {
                    X = 120,
                    Y = 60,
                    Width = 160,
                    Height = 80,
                    ImageBase64 = Convert.ToBase64String(png)
                }
            }
        };

        Inspect(Embed(source, overlay, "signed.pdf"), document =>
        {
            var render = PdfTestHarness.Render(document, 0);

            // The signature's top edge runs along display y = 60, inside x 120..280.
            var inside = PdfTestHarness.DarkestInRegion(render, 120, 60, 160, 80);
            Assert.True(inside < 120, $"expected signature ink inside its box, darkest was {inside}");

            // And nothing should have leaked outside it.
            var outside = PdfTestHarness.DarkestInRegion(render, 20, 200, 80, 80);
            Assert.True(outside > 240, $"signature leaked outside its box, darkest was {outside}");

            // The text layer must be untouched by the stamp.
            Assert.Contains("Hello searchable", PdfTestHarness.ExtractText(document, 0));
            return true;
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Ink_overlay_lands_at_the_same_display_position_for_every_rotation(int rotation)
    {
        var source = PdfTestHarness.CreateTextPdf(
            At($"rot{rotation}.pdf"),
            SourceText,
            rotation: rotation);

        var output = Embed(source, InkOverlay(), $"rot{rotation}-inked.pdf");

        Inspect(output, document =>
        {
            var render = PdfTestHarness.Render(document, 0);

            // Overlay coordinates are in display space, so the stroke must appear at the same
            // on-screen spot no matter how the page is rotated.
            var onStroke = PdfTestHarness.PixelAt(render, render.Width / 2, 200);
            Assert.True(onStroke.R > 180 && onStroke.G < 90 && onStroke.B < 90,
                $"rotation {rotation}: expected red stroke at y=200, got B={onStroke.B} G={onStroke.G} R={onStroke.R}");

            return true;
        });
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void Shapes_are_drawn_and_leave_the_text_layer_intact(ShapeKind kind)
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var overlay = new PageOverlayState
        {
            Shapes =
            {
                new ShapeOverlay
                {
                    Kind = kind,
                    Start = new PointOverlay { X = 60, Y = 120 },
                    End = new PointOverlay { X = 340, Y = 260 },
                    ColorHex = "#FF0000",
                    Thickness = 4
                }
            }
        };

        Inspect(Embed(source, overlay, $"shape-{kind}.pdf"), document =>
        {
            var render = PdfTestHarness.Render(document, 0);

            // Something red must have been drawn inside the shape's box.
            var darkest = PdfTestHarness.DarkestInRegion(render, 55, 115, 290, 150);
            Assert.True(darkest < 150, $"{kind}: nothing was drawn (darkest channel {darkest})");

            // And the page's own text must be untouched.
            Assert.Contains("Hello searchable", PdfTestHarness.ExtractText(document, 0));
            return true;
        });
    }

    [Fact]
    public void A_filled_shape_paints_its_interior()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var overlay = new PageOverlayState
        {
            Shapes =
            {
                new ShapeOverlay
                {
                    Kind = ShapeKind.Rectangle,
                    Start = new PointOverlay { X = 80, Y = 140 },
                    End = new PointOverlay { X = 320, Y = 250 },
                    ColorHex = "#FF0000",
                    FillColorHex = "#FF0000",
                    Thickness = 2
                }
            }
        };

        Inspect(Embed(source, overlay, "filled.pdf"), document =>
        {
            var render = PdfTestHarness.Render(document, 0);

            // Well inside the rectangle, away from its outline.
            var centre = PdfTestHarness.PixelAt(render, 200, 195);
            Assert.True(centre.R > centre.G && centre.R > centre.B,
                $"expected a red tint inside the shape, got B={centre.B} G={centre.G} R={centre.R}");
            Assert.True(centre.G < 250, "the interior should not still be blank paper");
            return true;
        });
    }

    [Fact]
    public void Pressure_varying_ink_is_embedded_as_a_tapered_ribbon()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);

        var points = new List<PointOverlay>();
        for (var index = 0; index <= 20; index++)
        {
            points.Add(new PointOverlay
            {
                X = 40 + (index * 15),
                Y = 200,
                // Heavy at the start, feathering out towards the end.
                Pressure = 1.0 - (index / 20.0 * 0.95)
            });
        }

        var overlay = new PageOverlayState
        {
            InkStrokes = { new InkStrokeOverlay { ColorHex = "#000000", Thickness = 14, Points = points } }
        };

        Inspect(Embed(source, overlay, "pressure.pdf"), document =>
        {
            var render = PdfTestHarness.Render(document, 0);

            var thickEnd = CountInkInColumn(render, x: 60);
            var thinEnd = CountInkInColumn(render, x: 320);

            Assert.True(thickEnd > 0 && thinEnd > 0, "the stroke should span the page");
            Assert.True(
                thickEnd > thinEnd + 2,
                $"stroke did not taper: {thickEnd}px at the heavy end vs {thinEnd}px at the light end");

            return true;
        });

        static int CountInkInColumn((byte[] Pixels, int Width, int Height) render, int x)
        {
            var count = 0;
            for (var y = 0; y < render.Height; y++)
            {
                var pixel = PdfTestHarness.PixelAt(render, x, y);
                if (pixel.R < 160 && pixel.G < 160 && pixel.B < 160)
                {
                    count++;
                }
            }

            return count;
        }
    }

    [Fact]
    public void Empty_overlays_are_not_treated_as_content()
    {
        Assert.False(PdfOverlayWriter.HasContent(new PageOverlayState()));

        // Whitespace-only text and single-point strokes are not real content either.
        var empty = new PageOverlayState
        {
            TextItems = { new TextOverlay { Text = "   " } },
            InkStrokes = { new InkStrokeOverlay { Points = { new PointOverlay { X = 1, Y = 1 } } } }
        };

        Assert.False(PdfOverlayWriter.HasContent(empty));
    }

    [Fact]
    public void Overlays_for_pages_outside_the_document_are_ignored()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), SourceText);
        var overlays = new PageOverlayDocument();
        overlays.Pages[7] = InkOverlay();

        var document = PdfTestHarness.Open(source);
        try
        {
            PdfOverlayWriter.WriteDocument(document, overlays, pageCount: 1);
            PdfTestHarness.Save(document, At("untouched.pdf"));
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }

        Assert.Contains("Hello searchable", Inspect(At("untouched.pdf"), d => PdfTestHarness.ExtractText(d, 0)));
    }
}
