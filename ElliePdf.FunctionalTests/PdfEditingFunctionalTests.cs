using System.Security.Cryptography.X509Certificates;
using ElliePdf.Models;
using ElliePdf.Services;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;

namespace ElliePdf.FunctionalTests;

public sealed class PdfEditingFunctionalTests
{
    [Fact]
    public async Task SaveWithOverlays_PreservesOriginalTextAndEmbedsSearchableEdit()
    {
        using var workspace = new TemporaryDirectory();
        var inputPath = Path.Combine(workspace.Path, "input.pdf");
        var outputPath = Path.Combine(workspace.Path, "edited.pdf");
        CreatePdf(inputPath, "Original searchable text");

        using var service = new PdfService();
        await using (var input = await service.OpenDocumentAsync(inputPath))
        {
            var overlays = new PageOverlayDocument
            {
                Pages =
                {
                    [0] = new PageOverlayState
                    {
                        TextItems =
                        [
                            new TextOverlay
                            {
                                X = 72,
                                Y = 150,
                                Width = 260,
                                Height = 40,
                                FontSize = 16,
                                Text = "Embedded edit marker"
                            }
                        ],
                        InkStrokes =
                        [
                            new InkStrokeOverlay
                            {
                                ColorHex = "#1A73E8",
                                Thickness = 3,
                                Points =
                                [
                                    new PointOverlay { X = 72, Y = 220 },
                                    new PointOverlay { X = 180, Y = 250 }
                                ]
                            }
                        ],
                        Signatures =
                        [
                            new SignatureOverlay
                            {
                                X = 72,
                                Y = 300,
                                Width = 120,
                                Height = 40,
                                ImageBase64 = CreateSignaturePng()
                            }
                        ]
                    }
                }
            };

            await service.SaveDocumentWithOverlaysAsync(input, overlays, outputPath);
        }

        Assert.True(File.Exists(outputPath));
        Assert.False(File.Exists(outputPath + ".ellie.json"));

        await using var edited = await service.OpenDocumentAsync(outputPath);
        Assert.NotEmpty(await service.SearchTextAsync(edited, "Original searchable text", false));
        Assert.NotEmpty(await service.SearchTextAsync(edited, "Embedded edit marker", false));
        var rendered = await service.RenderPageAsync(edited, 0, 1);
        Assert.NotEmpty(rendered.PngBytes);
    }

    [Fact]
    public async Task SaveTab_InPlaceReloadsEmbeddedPdfAndClearsPendingEdits()
    {
        using var workspace = new TemporaryDirectory();
        var pdfPath = Path.Combine(workspace.Path, "in-place.pdf");
        CreatePdf(pdfPath, "Before editing");

        var pdfService = new PdfService();
        try
        {
            var annotationStore = new AnnotationStore();
            var openService = new DocumentOpenService(pdfService, new NoPasswordPrompt());
            var saveService = new EditSaveService(
                pdfService,
                annotationStore,
                openService,
                new DigitalSignatureService());
            var tab = new DocumentTab(await openService.OpenAsync(pdfPath));
            annotationStore.SetPageOverlay(
                tab.Id,
                0,
                new PageOverlayState
                {
                    TextItems =
                    [
                        new TextOverlay
                        {
                            X = 72,
                            Y = 140,
                            Width = 220,
                            Height = 30,
                            Text = "Saved in place"
                        }
                    ]
                });

            await saveService.SaveTabAsync(tab, pdfPath);

            Assert.False(annotationStore.IsTabDirty(tab.Id));
            Assert.Empty(annotationStore.GetPageOverlay(tab.Id, 0).TextItems);
            Assert.NotEmpty(await pdfService.SearchTextAsync(tab.Session, "Saved in place", false));
            await tab.Session.DisposeAsync();
        }
        finally
        {
            pdfService.Dispose();
        }

        File.Delete(pdfPath);
        Assert.False(File.Exists(pdfPath));
    }

    [Fact]
    public async Task DigitalSignature_CreatesCertificateBackedPdfSignature()
    {
        using var workspace = new TemporaryDirectory();
        var inputPath = Path.Combine(workspace.Path, "unsigned.pdf");
        var outputPath = Path.Combine(workspace.Path, "signed.pdf");
        CreatePdf(inputPath, "Approve this document");

        var certificates = new CertificateService();
        var certificate = certificates.CreateSelfSignedCertificate(
            $"ElliePdf test {Guid.NewGuid():N}",
            "elliepdf-test@example.invalid");

        try
        {
            var signer = new DigitalSignatureService();
            await signer.SignAsync(
                inputPath,
                outputPath,
                new DigitalSignatureRequest(
                    certificate.Thumbprint,
                    0,
                    new PdfRect(72, 160, 310, 100),
                    "Functional test"));

            var pdfBytes = await File.ReadAllBytesAsync(outputPath);
            var pdfText = System.Text.Encoding.Latin1.GetString(pdfBytes);
            Assert.Contains("/ByteRange", pdfText, StringComparison.Ordinal);
            Assert.Contains("/Contents", pdfText, StringComparison.Ordinal);

            using var service = new PdfService();
            await using var signed = await service.OpenDocumentAsync(outputPath);
            Assert.NotEmpty(await service.SearchTextAsync(signed, "Approve this document", false));
            var fields = await service.GetFormFieldsAsync(signed, 0);
            var signatureField = Assert.Single(fields, field => field.Type == PdfFormFieldType.Signature);
            Assert.True(signatureField.IsSigned);
            Assert.False(signatureField.IsSignAction);
            Assert.InRange(signatureField.Bounds.Left, 70, 74);
            Assert.InRange(signatureField.Bounds.Right, 308, 312);
        }
        finally
        {
            RemoveCertificate(certificate.Thumbprint);
        }
    }

    [Fact]
    public void SignNamedPushButton_IsRecognizedAsSignAction()
    {
        var field = new PdfFormField(
            0,
            0,
            PdfFormFieldType.PushButton,
            "approve_button",
            "Sign here",
            string.Empty,
            new PdfRect(10, 30, 90, 10));

        Assert.True(field.IsSignAction);
    }

    private static void CreatePdf(string path, string text)
    {
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        using var document = new PdfDocument();
        var page = document.AddPage();
        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawString(
            text,
            new XFont("Arial", 18),
            XBrushes.Black,
            new XPoint(72, 100));
        document.Save(path);
    }

    private static string CreateSignaturePng()
    {
        using var bitmap = new System.Drawing.Bitmap(80, 24);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        using var pen = new System.Drawing.Pen(System.Drawing.Color.Black, 3);
        graphics.DrawLine(pen, 4, 18, 22, 4);
        graphics.DrawLine(pen, 22, 4, 40, 18);
        graphics.DrawLine(pen, 40, 18, 74, 6);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static void RemoveCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        foreach (var certificate in matches)
        {
            store.Remove(certificate);
            certificate.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ElliePdf-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class NoPasswordPrompt : IPdfPasswordPrompt
    {
        public Task<string?> PromptAsync(
            PdfPasswordPromptRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
