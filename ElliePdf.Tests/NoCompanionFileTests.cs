using System.Text.Json;
using ElliePdf.Models;
using ElliePdf.Services;
using Xunit;

namespace ElliePdf.Tests;

/// <summary>
/// Annotations must live in the PDF and nowhere else: a shared file has to be self-contained, and
/// nothing may drop a companion file beside it.
/// </summary>
public sealed class NoCompanionFileTests : IDisposable
{
    private readonly string _directory;

    public NoCompanionFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"elliepdf-nc-{Guid.NewGuid():N}");
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

    private static PageOverlayState Overlay(string text = "note") => new()
    {
        TextItems = { new TextOverlay { X = 20, Y = 40, Width = 200, Height = 30, Text = text } }
    };

    [Fact]
    public void Saving_annotations_leaves_no_extra_files_behind()
    {
        var source = PdfTestHarness.CreateTextPdf(At("source.pdf"), "Body text");

        var overlays = new PageOverlayDocument();
        overlays.Pages[0] = Overlay();

        var document = PdfTestHarness.Open(source);
        try
        {
            PdfOverlayWriter.WriteDocument(document, overlays, pageCount: 1);
            PdfTestHarness.Save(document, At("saved.pdf"));
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }

        // The directory must contain only the PDFs we created.
        var unexpected = Directory
            .GetFiles(_directory)
            .Where(path => !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(unexpected);
        Assert.False(File.Exists(At("saved.pdf.ellie.json")));
        Assert.False(File.Exists(At("source.pdf.ellie.json")));
    }

    [Fact]
    public void A_legacy_companion_is_imported_and_deleted()
    {
        var pdfPath = At("legacy.pdf");
        File.WriteAllText(pdfPath, "not a real pdf, only the path matters here");

        var legacy = new PageOverlayDocument();
        legacy.Pages[0] = Overlay("from the old sidecar");

        var companionPath = pdfPath + ".ellie.json";
        File.WriteAllText(
            companionPath,
            JsonSerializer.Serialize(legacy, ElliePdfJsonContext.Default.PageOverlayDocument));

        var current = new PageOverlayDocument();
        var imported = LegacyCompanionMigration.TryImport(pdfPath, current);

        Assert.True(imported);
        Assert.Equal("from the old sidecar", current.Pages[0].TextItems[0].Text);

        // And it must not survive the migration.
        Assert.False(File.Exists(companionPath));
    }

    [Fact]
    public void A_legacy_companion_merges_without_displacing_the_documents_own_annotations()
    {
        var pdfPath = At("merge.pdf");
        File.WriteAllText(pdfPath, "placeholder");

        var legacy = new PageOverlayDocument();
        legacy.Pages[0] = Overlay("sidecar note");
        File.WriteAllText(
            pdfPath + ".ellie.json",
            JsonSerializer.Serialize(legacy, ElliePdfJsonContext.Default.PageOverlayDocument));

        var current = new PageOverlayDocument();
        current.Pages[0] = Overlay("annotation already in the pdf");

        Assert.True(LegacyCompanionMigration.TryImport(pdfPath, current));

        var texts = current.Pages[0].TextItems.Select(item => item.Text).ToArray();
        Assert.Contains("annotation already in the pdf", texts);
        Assert.Contains("sidecar note", texts);
    }

    [Fact]
    public void Importing_is_a_no_op_when_there_is_no_companion()
    {
        var pdfPath = At("clean.pdf");
        File.WriteAllText(pdfPath, "placeholder");

        var current = new PageOverlayDocument();

        Assert.False(LegacyCompanionMigration.TryImport(pdfPath, current));
        Assert.Empty(current.Pages);
    }

    [Fact]
    public void A_corrupt_companion_is_ignored_rather_than_blocking_the_document()
    {
        var pdfPath = At("corrupt.pdf");
        File.WriteAllText(pdfPath, "placeholder");
        File.WriteAllText(pdfPath + ".ellie.json", "{ this is not json");

        var current = new PageOverlayDocument();

        Assert.False(LegacyCompanionMigration.TryImport(pdfPath, current));
        Assert.Empty(current.Pages);
    }
}
