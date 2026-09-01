using ElliePdf.Pdfium;
using Xunit;

namespace ElliePdf.Pdfium.IntegrationTests;

public sealed class FormSmokeTemporaryTests
{
    [Fact]
    public async Task Form_environment_outline_render_close()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "form-temporary-all-engine");
        await lane.InvokeAsync(engine =>
        {
            var source = File.OpenHandle(Fixture(), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var document = engine.LoadDocument(source, null, leaveOpen: false)!;
            using var form = engine.TryCreateFormEnvironment(document);
            var bookmark = engine.GetFirstBookmark(document);
            while (!bookmark.IsNull)
            {
                _ = engine.GetBookmarkPageIndex(document, bookmark);
                bookmark = engine.GetNextBookmark(document, bookmark);
            }
            using var page = engine.LoadPage(document, 1)!;
            using var bitmap = engine.CreateBitmap(514, 514);
            engine.FillBitmap(bitmap, 0, 0, 514, 514);
            engine.RenderPageRegion(page, bitmap, form, 0, 0, 612, 792, 0);
        });
    }

    private static string Fixture()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "testdata", "generated", "synthetic-mixed-orientation-links-forms-outlines.pdf");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException();
    }
}
