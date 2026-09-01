using System.IO.MemoryMappedFiles;
using ElliePdf.Pdf.Transport;

namespace ElliePdf.Pdfium.Worker.Tests;

public sealed class WorkerPixelLeasePoolTests
{
    [Fact]
    public async Task Publish_acknowledge_release_round_trips_pixels_and_closes_mapping()
    {
        var sessionId = Guid.NewGuid();
        await using var pool = new WorkerPixelLeasePool(sessionId, TimeSpan.FromSeconds(1));
        var buffer = CreateBuffer();

        var lease = pool.Publish(buffer);

        Assert.Equal(1, pool.Count);
        Assert.Equal(buffer.Pixels.Length, lease.ByteLength);
        Assert.Equal(sessionId, lease.SessionId);
        Assert.Equal(buffer.Key, lease.Key);
        Assert.Equal(buffer.Pixels, ReadPixels(lease));

        Assert.True(pool.Acknowledge(lease.LeaseId));
        Assert.False(pool.Acknowledge(lease.LeaseId));

        Assert.True(pool.Release(lease.LeaseId));
        Assert.False(pool.Release(lease.LeaseId));
        Assert.Equal(0, pool.Count);
        Assert.Throws<FileNotFoundException>(() => MemoryMappedFile.OpenExisting(lease.SharedMemoryId));
    }

    [Fact(Timeout = 10_000)]
    public async Task Unacknowledged_leases_expire_and_acknowledged_leases_get_a_fresh_timeout()
    {
        await using var pool = new WorkerPixelLeasePool(Guid.NewGuid(), TimeSpan.FromMilliseconds(250));

        var expired = pool.Publish(CreateBuffer());
        await WaitUntilAsync(() => pool.Count == 0, TimeSpan.FromSeconds(3));
        Assert.False(pool.Release(expired.LeaseId));

        var acknowledged = pool.Publish(CreateBuffer());
        await Task.Delay(150);
        Assert.True(pool.Acknowledge(acknowledged.LeaseId));

        await Task.Delay(175);
        Assert.Equal(1, pool.Count);

        await WaitUntilAsync(() => pool.Count == 0, TimeSpan.FromSeconds(3));
        Assert.False(pool.Release(acknowledged.LeaseId));
    }

    [Fact]
    public async Task Publish_rejects_out_of_bounds_pixel_buffers()
    {
        await using var pool = new WorkerPixelLeasePool(Guid.NewGuid(), TimeSpan.FromSeconds(1));
        var key = CreateKey();
        var invalid = new WorkerRenderedBuffer(
            new byte[4],
            Width: 2,
            Height: 1,
            Stride: 4,
            PixelFormat.Bgra8Premultiplied,
            key,
            RenderGeneration.Initial);

        Assert.Throws<TransportProtocolException>(() => pool.Publish(invalid));
    }

    private static byte[] ReadPixels(SharedMemoryLeaseMetadata lease)
    {
        using var mapping = MemoryMappedFile.OpenExisting(lease.SharedMemoryId, MemoryMappedFileRights.Read);
        using var view = mapping.CreateViewStream(lease.Offset, lease.ByteLength, MemoryMappedFileAccess.Read);
        var bytes = new byte[lease.ByteLength];
        _ = view.Read(bytes);
        return bytes;
    }

    private static WorkerRenderedBuffer CreateBuffer()
    {
        var key = CreateKey();
        return new WorkerRenderedBuffer(
            Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray(),
            Width: 2,
            Height: 4,
            Stride: 8,
            PixelFormat.Bgra8Premultiplied,
            key,
            RenderGeneration.Initial);
    }

    private static RenderKey CreateKey()
        => new(
            DocumentId.New(),
            PageId.New(),
            PageContentRevision.Initial,
            PageAppearanceRevision.Initial,
            new TileAddress(0, 0, 2, 4, 0),
            RasterScale64.FromPhysicalPixelsPerPoint(1),
            PageRotation.None,
            RenderMode.Normal);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met within the allotted time.");
            }

            await Task.Delay(25);
        }
    }
}
