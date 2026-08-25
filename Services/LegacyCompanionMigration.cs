using System.Text.Json;
using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// One-time migration for documents annotated by older builds, which kept annotations in a
/// <c>.ellie.json</c> file beside the PDF.
/// </summary>
/// <remarks>
/// Those files are gone: annotations now live inside the PDF. Anything still on disk is imported
/// once so no work is lost, then deleted so nothing is left beside the document.
/// </remarks>
internal static class LegacyCompanionMigration
{
    private const string CompanionSuffix = ".ellie.json";

    public static string GetCompanionPath(string pdfPath) => pdfPath + CompanionSuffix;

    /// <summary>
    /// Imports and deletes a legacy companion file if one exists, merging it over
    /// <paramref name="current"/>.
    /// </summary>
    /// <returns><c>true</c> when annotations were imported, meaning the tab has unsaved changes.</returns>
    public static bool TryImport(string pdfPath, PageOverlayDocument current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var companionPath = GetCompanionPath(pdfPath);
        if (!File.Exists(companionPath))
        {
            return false;
        }

        var imported = false;

        try
        {
            using var stream = File.OpenRead(companionPath);
            var legacy = JsonSerializer.Deserialize(stream, ElliePdfJsonContext.Default.PageOverlayDocument);

            if (legacy is not null)
            {
                foreach (var (pageIndex, state) in legacy.Pages)
                {
                    if (!PdfOverlayWriter.HasContent(state))
                    {
                        continue;
                    }

                    // The PDF's own annotations win; the companion only adds what is missing.
                    if (current.Pages.TryGetValue(pageIndex, out var existing))
                    {
                        existing.InkStrokes.AddRange(state.InkStrokes);
                        existing.Shapes.AddRange(state.Shapes);
                        existing.TextItems.AddRange(state.TextItems);
                        existing.Signatures.AddRange(state.Signatures);
                    }
                    else
                    {
                        current.Pages[pageIndex] = state;
                    }

                    imported = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable companion is not worth blocking the document for.
            return false;
        }

        Delete(pdfPath);
        return imported;
    }

    public static void Delete(string pdfPath)
    {
        try
        {
            var companionPath = GetCompanionPath(pdfPath);
            if (File.Exists(companionPath))
            {
                File.Delete(companionPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
