using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using Xunit;

namespace ElliePdf.Pdf.Contracts.Tests;

public sealed class IdentityAndLeaseTests
{
    [Fact]
    public void SnapshotDirtyStateIsDerivedFromRevisions()
    {
        var snapshot = new DocumentSnapshot(DocumentId.New(), new ContentRevision(2), new ContentRevision(1), StructureRevision.Initial, "sample.pdf", 1, 0, RecoveryState.None, ExternalFileState.Unchanged);
        Assert.True(snapshot.HasUnsavedChanges);
    }

    [Fact]
    public async Task PixelLeaseReleaseRunsExactlyOnce()
    {
        var releases = 0;
        var lease = new PixelBufferLease(Guid.NewGuid(), "buffer", 0, 16, 2, 2, 8, PixelFormat.Bgra8Premultiplied, NewRenderKey(), () =>
        {
            Interlocked.Increment(ref releases);
            return ValueTask.CompletedTask;
        });

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(1, releases);
    }

    private static RenderKey NewRenderKey() => new(DocumentId.New(), PageId.New(), PageContentRevision.Initial, PageAppearanceRevision.Initial, new TileAddress(0, 0, 1, 1, 1), new RasterScale64(64), PageRotation.None, RenderMode.Normal);
}
