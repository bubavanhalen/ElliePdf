using System.Reflection;
using System.Security.Cryptography;
using ElliePdf.Pdfium;
using Xunit;

namespace ElliePdf.Pdfium.IntegrationTests;

public sealed class PdfiumIntegrationTests
{
    [Fact]
    public void Pinned_x64_asset_is_present_and_verified_in_test_output()
    {
        var path = PdfiumAssetVerifier.GetAppPrivatePath(AppContext.BaseDirectory);
        Assert.True(File.Exists(path), $"Expected app-private asset at {path}.");
        PdfiumAssetVerifier.VerifyAppPrivateAsset(AppContext.BaseDirectory, PdfiumKnownAssets.WinX64);
        Assert.Equal(PdfiumKnownAssets.WinX64.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public async Task Engine_lane_starts_on_a_dedicated_thread_and_runs_native_work()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "integration-engine");
        await lane.Ready;
        var result = await lane.InvokeAsync(engine =>
        {
            Assert.Equal(Environment.CurrentManagedThreadId, engine.EngineThreadId);
            return (ThreadId: engine.EngineThreadId, PageCount: Load(engine, Fixture("synthetic-vector-small.pdf"), null));
        });
        Assert.NotEqual(Environment.CurrentManagedThreadId, result.ThreadId);
        Assert.Equal(3, result.PageCount);
    }

    [Fact]
    public async Task Vector_fixture_supports_render_text_search_outline_save_and_reopen()
    {
        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ellie-pdfium-{Guid.NewGuid():N}.pdf");
        try
        {
            await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "semantic-engine");
            await lane.InvokeAsync(engine =>
            {
                using var document = RequireDocument(engine, Fixture("synthetic-vector-small.pdf"));
                Assert.Equal(3, engine.GetPageCount(document));
                using var page = RequirePage(engine, document, 0);
                var size = engine.GetPageSize(page);
                Assert.True(size.Width > 0 && size.Height > 0);
                using var bitmap = engine.CreateBitmap(320, 240);
                engine.FillBitmap(bitmap, 0, 0, 320, 240);
                engine.RenderPage(page, bitmap, null, 320, 240);
                Assert.Contains(engine.CopyBitmapBytes(bitmap, checked(engine.GetBitmapStride(bitmap) * 240)), static b => b != 0);
                using var text = Assert.IsType<PdfiumTextPageHandle>(engine.LoadTextPage(page));
                var extracted = engine.GetText(text, 0, engine.CountCharacters(text));
                Assert.Contains("ElliePdf", extracted, StringComparison.Ordinal);
                using var search = Assert.IsType<PdfiumSearchHandle>(engine.StartSearch(text, "ElliePdf", matchCase: false));
                Assert.True(engine.FindNext(search));
                var match = engine.GetSearchResult(search);
                Assert.True(match.CharacterIndex >= 0);
                Assert.True(match.Length > 0);
                _ = engine.TryGetTextRect(text, match.CharacterIndex, out _, out _, out _, out _);
                using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                engine.SaveAsCopy(document, stream);
            });
            await lane.InvokeAsync(engine =>
            {
                using var reopened = RequireDocument(engine, output);
                Assert.Equal(3, engine.GetPageCount(reopened));
                using var page = RequirePage(engine, reopened, 0);
                using var text = Assert.IsType<PdfiumTextPageHandle>(engine.LoadTextPage(page));
                Assert.Contains("ElliePdf", engine.GetText(text, 0, engine.CountCharacters(text)), StringComparison.Ordinal);
            });
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task Mixed_fixture_preserves_outline_and_rotation_metadata()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "outline-engine");
        await lane.InvokeAsync(engine =>
        {
            using var document = RequireDocument(engine, Fixture("synthetic-mixed-orientation-links-forms-outlines.pdf"));
            Assert.Equal(8, engine.GetPageCount(document));
            using var page = RequirePage(engine, document, 1);
            var pageSize = engine.GetPageSize(page);
            Assert.True(pageSize.Width > pageSize.Height);
            var titles = new List<string>();
            var count = TraverseBookmarks(engine, document, default, titles);
            Assert.Equal(8, count);
            Assert.Equal(
                Enumerable.Range(1, 8).Select(static index => $"Synthetic section {index}"),
                titles);
            Assert.True(count >= 1);
        });
    }

    [Fact]
    public async Task Mixed_fixture_exposes_internal_safe_and_blocked_links_and_form_fields()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "semantic-links-engine");
        await lane.InvokeAsync(engine =>
        {
            using var document = RequireDocument(engine, Fixture("synthetic-mixed-orientation-links-forms-outlines.pdf"));
            using var form = Assert.IsType<PdfiumFormHandle>(engine.TryCreateFormEnvironment(document));

            using var page0 = RequirePage(engine, document, 0);
            var links = engine.GetPageLinks(document, page0);
            Assert.Equal(3, links.Count);
            Assert.Contains(links, link => link.Kind == PdfiumLinkActionKind.InternalDestination && link.DestinationPageIndex == 1);
            Assert.Contains(links, link => link.Kind == PdfiumLinkActionKind.Uri && link.Uri == "https://example.invalid/elliepdf");
            Assert.Contains(links, link => link.Kind == PdfiumLinkActionKind.Uri && link.Uri == "javascript:alert('blocked')");

            using var page1 = RequirePage(engine, document, 1);
            using var page2 = RequirePage(engine, document, 2);
            using var page4 = RequirePage(engine, document, 4);
            using var page5 = RequirePage(engine, document, 5);

            var checkboxField = Assert.Single(engine.GetPageFormFields(page1, form), static field => field.Name == "checkbox_field");
            Assert.Equal(2, checkboxField.NativeFieldType);
            Assert.False(checkboxField.IsChecked);

            var comboField = Assert.Single(engine.GetPageFormFields(page2, form), static field => field.Name == "combo_field");
            Assert.Equal(4, comboField.NativeFieldType);
            Assert.Equal(["Alpha", "Beta", "Gamma"], comboField.Options);
            Assert.Equal("Beta", comboField.Value);

            var readOnlyField = Assert.Single(engine.GetPageFormFields(page4, form), static field => field.Name == "readonly_field");
            Assert.Equal(1, readOnlyField.Flags & 1);

            var unsafeField = Assert.Single(engine.GetPageFormFields(page5, form), static field => field.Name == "unsafe_text_field");
            Assert.True(unsafeField.HasUnsafeAction);
        });
    }

    [Fact]
    public async Task Form_updates_can_be_saved_and_reopened_for_text_checkbox_and_choice_fields()
    {
        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ellie-pdfium-form-save-{Guid.NewGuid():N}.pdf");
        try
        {
            await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "semantic-save-engine");
            await lane.InvokeAsync(engine =>
            {
                using var document = RequireDocument(engine, Fixture("synthetic-mixed-orientation-links-forms-outlines.pdf"));
                using var form = Assert.IsType<PdfiumFormHandle>(engine.TryCreateFormEnvironment(document));

                UpdateField(engine, document, form, 0, "text_field", "V", "Saved text value");
                UpdateField(engine, document, form, 1, "checkbox_field", "V", "Yes");
                UpdateField(engine, document, form, 1, "checkbox_field", "AS", "Yes");
                UpdateField(engine, document, form, 2, "combo_field", "V", "Gamma");

                using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                engine.SaveAsCopy(document, stream);
            });

            await lane.InvokeAsync(engine =>
            {
                using var reopened = RequireDocument(engine, output);
                using var form = Assert.IsType<PdfiumFormHandle>(engine.TryCreateFormEnvironment(reopened));
                using var page0 = RequirePage(engine, reopened, 0);
                using var page1 = RequirePage(engine, reopened, 1);
                using var page2 = RequirePage(engine, reopened, 2);

                var textField = Assert.Single(engine.GetPageFormFields(page0, form), static field => field.Name == "text_field");
                Assert.Equal("Saved text value", textField.Value);

                var checkboxField = Assert.Single(engine.GetPageFormFields(page1, form), static field => field.Name == "checkbox_field");
                Assert.True(checkboxField.IsChecked);
                Assert.Equal("Yes", checkboxField.Value);

                var comboField = Assert.Single(engine.GetPageFormFields(page2, form), static field => field.Name == "combo_field");
                Assert.Equal("Gamma", comboField.Value);
            });
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task Encrypted_fixture_rejects_wrong_password_and_opens_with_correct_password()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "encryption-engine");
        await lane.InvokeAsync(engine =>
        {
            Assert.Null(engine.LoadDocument(Fixture("synthetic-encrypted.pdf"), "wrong-password"));
            using var document = RequireDocument(engine, Fixture("synthetic-encrypted.pdf"), "ellie-test");
            Assert.Equal(2, engine.GetPageCount(document));
        });
    }

    [Fact]
    public async Task Corrupt_fixture_fails_closed_and_huge_media_box_does_not_allocate_unbounded_bitmap()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "bounds-engine");
        await lane.InvokeAsync(engine =>
        {
            Assert.Null(engine.LoadDocument(Fixture("synthetic-corrupt.pdf"), null));
            using var document = RequireDocument(engine, Fixture("synthetic-huge-mediabox.pdf"));
            using var page = RequirePage(engine, document, 0);
            var size = engine.GetPageSize(page);
            Assert.True(size.Width > 100_000 || size.Height > 100_000);
            using var bitmap = engine.CreateBitmap(64, 64);
            engine.RenderPage(page, bitmap, null, 64, 64);
        });
    }

    [Fact]
    public async Task Bitmap_limits_reject_oversized_dimensions_and_allocations_before_native_use()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "bitmap-limits-engine");
        await lane.InvokeAsync(engine =>
        {
            var dimension = Assert.Throws<PdfiumResourceLimitException>(
                () => engine.CreateBitmap(PdfiumEngine.MaximumBitmapDimension + 1, 1));
            Assert.Contains("dimension", dimension.Message, StringComparison.OrdinalIgnoreCase);

            var allocation = Assert.Throws<PdfiumResourceLimitException>(
                () => engine.CreateBitmap(4_096, 1_025));
            Assert.Contains("allocation", allocation.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, engine.ActiveOwnerCount);
        });
    }

    [Fact]
    public async Task Bitmap_copy_and_row_writes_reject_buffer_overruns_and_small_bitmap_is_usable()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "bitmap-boundary-engine");
        await lane.InvokeAsync(engine =>
        {
            Assert.Equal(0, engine.ActiveOwnerCount);
            using (var bitmap = engine.CreateBitmap(64, 32))
            {
                Assert.True(bitmap.Stride >= 64 * 4);
                Assert.Equal(bitmap.ByteLength, engine.CopyBitmapBytes(bitmap, bitmap.ByteLength).Length);
                Assert.Throws<PdfiumResourceLimitException>(() => engine.CopyBitmapBytes(bitmap, bitmap.ByteLength + 1));

                var row = new byte[bitmap.Stride];
                engine.WriteBitmapRow(bitmap, 0, row, 0, row.Length);
                Assert.Throws<ArgumentException>(() => engine.WriteBitmapRow(bitmap, bitmap.ByteLength, row, 0, 1));
                Assert.Throws<ArgumentException>(() => engine.WriteBitmapRow(bitmap, 0, row, row.Length, 1));
            }

            Assert.Equal(0, engine.ActiveOwnerCount);
        });
    }

    [Fact]
    public async Task Scoped_native_owners_are_counted_and_return_to_zero_after_disposal()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "owner-count-engine");
        await lane.InvokeAsync(engine =>
        {
            Assert.Equal(0, engine.ActiveOwnerCount);
            using (var document = RequireDocument(engine, Fixture("synthetic-vector-small.pdf")))
            using (var page = RequirePage(engine, document, 0))
            using (var bitmap = engine.CreateBitmap(32, 32))
            {
                Assert.True(engine.ActiveOwnerCount >= 3);
                engine.RenderPage(page, bitmap, null, 32, 32);
            }

            Assert.Equal(0, engine.ActiveOwnerCount);
        });
    }

    [Fact]
    public async Task Owners_close_deterministically_are_lane_bound_and_double_dispose_is_safe()
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "owner-engine");
        PdfiumDocumentHandle? document = null;
        await lane.InvokeAsync(engine => document = RequireDocument(engine, Fixture("synthetic-vector-small.pdf")));
        var offLane = Assert.Throws<InvalidOperationException>(() => document!.Dispose());
        Assert.Contains("engine lane", offLane.Message, StringComparison.OrdinalIgnoreCase);
        await lane.InvokeAsync(_ =>
        {
            document!.Dispose();
            document.Dispose();
            Assert.True(document.IsClosed);
        });
    }

    [Fact]
    public async Task Repeated_engine_lane_initialization_balances_native_library_references()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, $"lifecycle-engine-{iteration}");
            await lane.InvokeAsync(engine =>
            {
                using var document = RequireDocument(engine, Fixture("synthetic-vector-small.pdf"));
                Assert.True(engine.GetPageCount(document) > 0);
            });
        }
    }

    [Fact]
    public void Native_owner_types_do_not_declare_finalizers()
    {
        var ownerTypes = new[] { typeof(PdfiumDocumentHandle), typeof(PdfiumPageHandle), typeof(PdfiumBitmapHandle), typeof(PdfiumTextPageHandle), typeof(PdfiumSearchHandle), typeof(PdfiumPageObjectHandle), typeof(PdfiumFormHandle) };
        foreach (var type in ownerTypes)
            Assert.Null(type.GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void Asset_verifier_rejects_missing_decoy_hash_and_wrong_pe_assets()
    {
        using var temp = new TemporaryDirectory();
        var path = PdfiumAssetVerifier.GetAppPrivatePath(temp.Path);
        var valid = File.ReadAllBytes(PdfiumAssetVerifier.GetAppPrivatePath(AppContext.BaseDirectory));
        Assert.Throws<FileNotFoundException>(() => PdfiumAssetVerifier.VerifyAppPrivateAsset(temp.Path, PdfiumKnownAssets.WinX64));
        File.WriteAllBytes(path, new byte[valid.Length]);
        Assert.Throws<BadImageFormatException>(() => PdfiumAssetVerifier.VerifyAppPrivateAsset(temp.Path, PdfiumKnownAssets.WinX64));
        valid[0x1000] ^= 1;
        File.WriteAllBytes(path, valid);
        Assert.Throws<BadImageFormatException>(() => PdfiumAssetVerifier.VerifyAppPrivateAsset(temp.Path, PdfiumKnownAssets.WinX64));

        valid = File.ReadAllBytes(PdfiumAssetVerifier.GetAppPrivatePath(AppContext.BaseDirectory));
        valid[0] = (byte)'X';
        File.WriteAllBytes(path, valid);
        Assert.Throws<BadImageFormatException>(() => PdfiumAssetVerifier.VerifyAppPrivateAsset(temp.Path, PdfiumKnownAssets.WinX64));
    }

    [Fact]
    public void Asset_verifier_rejects_reparse_point_when_the_platform_allows_creating_one()
    {
        using var temp = new TemporaryDirectory();
        var link = PdfiumAssetVerifier.GetAppPrivatePath(temp.Path);
        var target = PdfiumAssetVerifier.GetAppPrivatePath(AppContext.BaseDirectory);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }
        Assert.Throws<BadImageFormatException>(() => PdfiumAssetVerifier.VerifyAppPrivateAsset(temp.Path, PdfiumKnownAssets.WinX64));
    }

    private static string Fixture(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = System.IO.Path.Combine(current.FullName, "testdata", "generated", name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Generated test fixture was not found: {name}");
    }

    private static PdfiumDocumentHandle RequireDocument(PdfiumEngine engine, string path, string? password = null) => engine.LoadDocument(path, password) ?? throw new InvalidOperationException($"PDFium failed to open {path}.");
    private static PdfiumPageHandle RequirePage(PdfiumEngine engine, PdfiumDocumentHandle document, int index) => engine.LoadPage(document, index) ?? throw new InvalidOperationException($"PDFium failed to load page {index}.");
    private static int Load(PdfiumEngine engine, string path, string? password) { using var document = RequireDocument(engine, path, password); return engine.GetPageCount(document); }
    private static void UpdateField(PdfiumEngine engine, PdfiumDocumentHandle document, PdfiumFormHandle form, int pageIndex, string fieldName, string key, string value)
    {
        using var page = RequirePage(engine, document, pageIndex);
        var field = Assert.Single(engine.GetPageFormFields(page, form), info => info.Name == fieldName);
        using var annotation = Assert.IsType<PdfiumAnnotationHandle>(engine.GetPageAnnotation(page, field.AnnotationIndex));
        Assert.True(engine.SetAnnotationStringValue(annotation, key, value), $"Unable to update {fieldName} {key}.");
    }

    private static int TraverseBookmarks(PdfiumEngine engine, PdfiumDocumentHandle document, PdfiumBookmark parent, List<string> titles)
    {
        var count = 0;
        var bookmark = engine.GetFirstBookmark(document, parent);
        while (!bookmark.IsNull)
        {
            count++;
            var title = engine.GetBookmarkTitle(bookmark);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.StartsWith("Synthetic section ", title, StringComparison.Ordinal);
            Assert.InRange(engine.GetBookmarkPageIndex(document, bookmark), 0, 7);
            titles.Add(title);
            count += TraverseBookmarks(engine, document, bookmark, titles);
            bookmark = engine.GetNextBookmark(document, bookmark);
        }

        return count;
    }
    private static void TryDelete(string path) { if (File.Exists(path)) File.Delete(path); }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ellie-pdfium-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; } = null!;
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
