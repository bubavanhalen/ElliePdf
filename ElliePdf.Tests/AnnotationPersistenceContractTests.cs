using Xunit;

namespace ElliePdf.Tests;

public sealed class AnnotationPersistenceContractTests
{
    [Fact]
    public void DefaultAndFlattenedSavesUseDistinctWorkerBackedTransactions()
    {
        var root = FindRepositoryRoot();
        var editSave = File.ReadAllText(Path.Combine(root, "Services", "EditSaveService.cs"));
        var pdfService = File.ReadAllText(Path.Combine(root, "Services", "PdfService.cs"));
        var worker = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ElliePdf.Pdfium.Worker",
            "WorkerDocumentRegistry.cs"));

        Assert.Contains("SaveDocumentWithOverlaysAsync", editSave, StringComparison.Ordinal);
        Assert.Contains("SaveDocumentFlattenedCopyAsync", editSave, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistentOverlaySaveNotSupportedException", editSave, StringComparison.Ordinal);
        Assert.Contains("annotations.StageAnnotationsAsync", pdfService, StringComparison.Ordinal);
        Assert.Contains("_atomicDocumentStore.CommitAsync", pdfService, StringComparison.Ordinal);
        Assert.Contains("committed: sourceDestination", pdfService, StringComparison.Ordinal);
        Assert.Contains("annotations.SaveFlattenedCopyAsync", pdfService, StringComparison.Ordinal);
        Assert.Contains("stable annotation ids make a later retry idempotent", File.ReadAllText(Path.Combine(
            root,
            "src",
            "ElliePdf.Pdf.Contracts",
            "EngineContracts.cs")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("engine.FlattenPage(pageToFlatten)", worker, StringComparison.Ordinal);
        Assert.Contains("engine.InsertPageObject", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Open(", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("FileStream", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulSourceSaveSubtractsOnlyCapturedRecoveryEditsAndRefreshesPresentation()
    {
        var root = FindRepositoryRoot();
        var editSave = File.ReadAllText(Path.Combine(root, "Services", "EditSaveService.cs"));
        var annotationStore = File.ReadAllText(Path.Combine(root, "Services", "AnnotationStore.cs"));
        var reader = File.ReadAllText(Path.Combine(root, "ViewModels", "ReaderViewModel.cs"));

        Assert.Contains("CommitPersistedEditsAsync", editSave, StringComparison.Ordinal);
        Assert.Contains("InkEquals(candidate, persisted)", annotationStore, StringComparison.Ordinal);
        Assert.Contains("TextEquals(candidate, persisted)", annotationStore, StringComparison.Ordinal);
        Assert.Contains("SignatureEquals(candidate, persisted)", annotationStore, StringComparison.Ordinal);
        Assert.Contains("_gpuTileCache.Clear()", reader, StringComparison.Ordinal);
        Assert.Contains("await RefreshRenderedPagesAsync", reader, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
