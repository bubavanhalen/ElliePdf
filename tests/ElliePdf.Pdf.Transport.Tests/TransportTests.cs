using System.Text.Json;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Pdf.Transport;
using Xunit;

namespace ElliePdf.Pdf.Transport.Tests;

public sealed class TransportTests
{
    [Fact]
    public void WorkerOperationIdsRemainStableAcrossAdditiveProtocolChanges()
    {
        Assert.Equal(0, (int)WorkerOperation.OpenDocument);
        Assert.Equal(15, (int)WorkerOperation.SaveDocument);
        Assert.Equal(16, (int)WorkerOperation.CloseDocument);
        Assert.Equal(17, (int)WorkerOperation.Shutdown);
        Assert.Equal(18, (int)WorkerOperation.StageAnnotations);
        Assert.Equal(19, (int)WorkerOperation.FinalizeAnnotationTransaction);
        Assert.Equal(20, (int)WorkerOperation.SaveFlattenedCopy);
    }

    [Fact]
    public async Task FrameRoundTripsAndRequiresExactSecret()
    {
        var secret = LaunchSecret.Generate();
        var identity = TransportIdentity.ForSession(Guid.NewGuid());
        using var json = JsonDocument.Parse("{\"operation\":\"heartbeat\"}");
        var envelope = TransportEnvelope.Create(TransportMessageKind.Heartbeat, secret.ToArray(), identity, json.RootElement.Clone());
        var codec = new LengthPrefixedFrameCodec();
        await using var stream = new MemoryStream();
        await codec.WriteAsync(stream, envelope);
        stream.Position = 0;
        var result = await codec.ReadAsync(stream);
        Assert.Equal(envelope.Kind, result!.Kind);
        Assert.Equal(envelope.CorrelationId, result.CorrelationId);
        Assert.Equal(envelope.Secret, result.Secret);
    }

    [Fact]
    public async Task EOFBeforePrefixIsNullButTruncatedFrameFails()
    {
        var codec = new LengthPrefixedFrameCodec();
        Assert.Null(await codec.ReadAsync(new MemoryStream()));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await codec.ReadAsync(new MemoryStream([1, 0])));
    }

    [Fact]
    public async Task OversizedLengthIsRejectedBeforeAllocation()
    {
        var codec = new LengthPrefixedFrameCodec(new TransportCodecOptions { MaxFrameBytes = 32 });
        await Assert.ThrowsAsync<TransportProtocolException>(async () => await codec.ReadAsync(new MemoryStream([33, 0, 0, 0])));
    }

    [Fact]
    public void AuthenticationAndStaleIdentityAreRejected()
    {
        var session = Guid.NewGuid();
        var secret = LaunchSecret.Generate();
        var validator = new TransportIdentityValidator(session, secret);
        using var json = JsonDocument.Parse("null");
        var current = TransportEnvelope.Create(TransportMessageKind.Request, secret.ToArray(),
            TransportIdentity.ForDocument(session, DocumentId.New(), new ContentRevision(4)), json.RootElement.Clone());
        validator.Validate(current, updateWatermark: true);
        var staleIdentity = current.Identity with { ContentRevision = 3 };
        var stale = current with { Identity = staleIdentity };
        Assert.Throws<TransportProtocolException>(() => validator.Validate(stale));
        Assert.Throws<TransportProtocolException>(() => validator.Validate(current with { Secret = new byte[32] }));
    }

    [Fact]
    public async Task CodecRejectsNestedArraysPastConfiguredLimitButAllowsEmptyArrays()
    {
        var codec = new LengthPrefixedFrameCodec(new TransportCodecOptions { MaxArrayLength = 2, MaxDepth = 8 });
        var allowed = CreateEnvelopeFrame("""{"items":[[],[]]}""");
        var envelope = await codec.ReadAsync(new MemoryStream(allowed));
        Assert.Equal(JsonValueKind.Array, envelope!.Payload.GetProperty("items").ValueKind);
        Assert.Equal(2, envelope.Payload.GetProperty("items").GetArrayLength());

        var rejected = CreateEnvelopeFrame("""{"items":[[],[],[]]}""");
        await Assert.ThrowsAsync<TransportProtocolException>(async () => await codec.ReadAsync(new MemoryStream(rejected)));
    }

    [Fact]
    public void PageWatermarksAreIndependentAcrossPages()
    {
        var session = Guid.NewGuid();
        var secret = LaunchSecret.Generate();
        var validator = new TransportIdentityValidator(session, secret);
        var documentId = DocumentId.New();
        var page1 = PageId.New();
        var page2 = PageId.New();
        using var json = JsonDocument.Parse("null");

        var firstPage = TransportEnvelope.Create(
            TransportMessageKind.Response,
            secret.ToArray(),
            TransportIdentity.ForRender(
                session,
                new RenderKey(documentId, page1, PageContentRevision.Initial, PageAppearanceRevision.Initial, new TileAddress(0, 0, 2, 2, 1), new RasterScale64(64), PageRotation.None, RenderMode.Normal),
                new RenderGeneration(10)),
            json.RootElement.Clone());

        var secondPage = TransportEnvelope.Create(
            TransportMessageKind.Response,
            secret.ToArray(),
            TransportIdentity.ForRender(
                session,
                new RenderKey(documentId, page2, PageContentRevision.Initial, PageAppearanceRevision.Initial, new TileAddress(0, 0, 2, 2, 1), new RasterScale64(64), PageRotation.None, RenderMode.Normal),
                RenderGeneration.Initial),
            json.RootElement.Clone());

        validator.Validate(firstPage, updateWatermark: true);
        validator.Validate(secondPage, updateWatermark: true);

        var staleFirstPage = firstPage with { Identity = firstPage.Identity with { RenderGeneration = 9 } };
        Assert.Throws<TransportProtocolException>(() => validator.Validate(staleFirstPage));
    }

    [Fact]
    public void ForgetDocumentResetsDocumentAndPageWatermarks()
    {
        var session = Guid.NewGuid();
        var secret = LaunchSecret.Generate();
        var validator = new TransportIdentityValidator(session, secret);
        var documentId = DocumentId.New();
        var pageId = PageId.New();
        using var json = JsonDocument.Parse("null");

        var current = TransportEnvelope.Create(
            TransportMessageKind.Response,
            secret.ToArray(),
            new TransportIdentity
            {
                SessionId = session,
                DocumentId = documentId.Value,
                PageId = pageId.Value,
                ContentRevision = 5,
                PageContentRevision = 4,
                PageAppearanceRevision = 3,
                SearchGeneration = 2
            },
            json.RootElement.Clone());

        validator.Validate(current, updateWatermark: true);
        validator.ForgetDocument(documentId.Value);

        var olderIdentity = current with
        {
            Identity = current.Identity with
            {
                ContentRevision = 1,
                PageContentRevision = 1,
                PageAppearanceRevision = 1,
                SearchGeneration = 1
            }
        };

        validator.Validate(olderIdentity, updateWatermark: true);
    }

    [Fact]
    public void BrokeredHandleDescriptorValidatesAuthority()
    {
        var now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        new BrokeredHandleDescriptor
        {
            HandleId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            NativeHandleValue = 42,
            Access = BrokeredHandleAccess.ReadOnlySource,
            ExpiresAtUtc = now.AddMinutes(1)
        }.Validate(now);

        Assert.Throws<TransportProtocolException>(() => new BrokeredHandleDescriptor
        {
            HandleId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            NativeHandleValue = 0,
            Access = BrokeredHandleAccess.ReadOnlySource
        }.Validate(now));

        Assert.Throws<TransportProtocolException>(() => new BrokeredHandleDescriptor
        {
            HandleId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            NativeHandleValue = 7,
            Access = BrokeredHandleAccess.TemporaryWrite
        }.Validate(now));

        Assert.Throws<TransportProtocolException>(() => new BrokeredHandleDescriptor
        {
            HandleId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            NativeHandleValue = 7,
            Access = BrokeredHandleAccess.ReadOnlySource,
            TransactionId = Guid.NewGuid()
        }.Validate(now));

        Assert.Throws<TransportProtocolException>(() => new BrokeredHandleDescriptor
        {
            HandleId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            NativeHandleValue = 7,
            Access = BrokeredHandleAccess.ReadOnlySource,
            ExpiresAtUtc = now
        }.Validate(now));
    }

    [Fact]
    public void OrderedMergeProtocolCarriesOnlyStableIdentitiesAndTransactionAuthority()
    {
        var sessionId = Guid.NewGuid();
        var documentId = DocumentId.New();
        var pageId = PageId.New();
        var transactionId = Guid.NewGuid();
        var command = new MergeOrderedPagesCommand(
            new MergeOrderedPagesRequest(
            [
                new PageMergeReference(
                    documentId,
                    pageId,
                    new ContentRevision(4),
                    new StructureRevision(2),
                    new PageContentRevision(3))
            ]),
            new BrokeredHandleDescriptor
            {
                HandleId = Guid.NewGuid(),
                SessionId = sessionId,
                NativeHandleValue = 42,
                Access = BrokeredHandleAccess.TemporaryWrite,
                TransactionId = transactionId,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
            });

        var json = JsonSerializer.Serialize(
            command,
            WorkerProtocolJsonContext.Default.MergeOrderedPagesCommand);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pdf", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(transactionId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);

        var roundTrip = JsonSerializer.Deserialize(
            json,
            WorkerProtocolJsonContext.Default.MergeOrderedPagesCommand);
        Assert.NotNull(roundTrip);
        roundTrip.TargetHandle.Validate();
        Assert.Equal(pageId, Assert.Single(roundTrip.Request.PagesInOrder).PageId);
    }

    [Fact]
    public void AnnotationProtocolRoundTripsValueOnlyPayloadAndBrokeredWriteAuthority()
    {
        var sessionId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var documentId = DocumentId.New();
        var pageId = PageId.New();
        var request = new PdfAnnotationSaveRequest(
            transactionId,
            documentId,
            new ContentRevision(3),
            new StructureRevision(2),
            [
                new PdfPageOverlayBatch(
                    1,
                    pageId,
                    new PageContentRevision(4),
                    [new PdfInkAnnotation(
                        "ellie:ink:transport",
                        [new PdfOverlayPoint(1, 2), new PdfOverlayPoint(3, 4)],
                        new PdfOverlayColor(5, 6, 7, 8),
                        2)],
                    [new PdfTextStampAnnotation(
                        "ellie:text:transport",
                        new PdfOverlayRectangle(10, 20, 30, 40),
                        "transport note",
                        12,
                        new PdfOverlayColor(9, 10, 11),
                        true,
                        false)],
                    [])
            ]);
        var descriptor = new BrokeredHandleDescriptor
        {
            HandleId = Guid.NewGuid(),
            SessionId = sessionId,
            NativeHandleValue = 42,
            Access = BrokeredHandleAccess.TemporaryWrite,
            TransactionId = transactionId,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        var command = new StageAnnotationsCommand(request, descriptor);

        var json = JsonSerializer.Serialize(command, WorkerProtocolJsonContext.Default.StageAnnotationsCommand);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pdf", json, StringComparison.OrdinalIgnoreCase);
        var roundTrip = JsonSerializer.Deserialize(json, WorkerProtocolJsonContext.Default.StageAnnotationsCommand);

        Assert.NotNull(roundTrip);
        roundTrip.TargetHandle.Validate();
        Assert.Equal(transactionId, roundTrip.Request.TransactionId);
        Assert.Equal(pageId, Assert.Single(roundTrip.Request.Pages).PageId);
        Assert.Equal("transport note", Assert.Single(Assert.Single(roundTrip.Request.Pages).Text).Text);

        var finalize = new FinalizeAnnotationTransactionCommand(documentId, transactionId, true);
        var finalizeJson = JsonSerializer.Serialize(
            finalize,
            WorkerProtocolJsonContext.Default.FinalizeAnnotationTransactionCommand);
        Assert.Equal(
            finalize,
            JsonSerializer.Deserialize(
                finalizeJson,
                WorkerProtocolJsonContext.Default.FinalizeAnnotationTransactionCommand));
        var flatten = new SaveFlattenedCopyCommand(request, descriptor);
        var flattenJson = JsonSerializer.Serialize(flatten, WorkerProtocolJsonContext.Default.SaveFlattenedCopyCommand);
        Assert.Equal(
            transactionId,
            JsonSerializer.Deserialize(
                flattenJson,
                WorkerProtocolJsonContext.Default.SaveFlattenedCopyCommand)!.Request.TransactionId);
    }

    [Fact]
    public void SharedMemoryLeaseRespectsSixteenMiBLimits()
    {
        var metadata = NewLeaseMetadata(Guid.NewGuid(), Guid.NewGuid()) with
        {
            MappingLength = 16L * 1024 * 1024,
            ByteLength = 16 * 1024 * 1024,
            Width = 1024,
            Height = 4096,
            Stride = 4096
        };

        metadata.Validate();

        Assert.Throws<TransportProtocolException>(() => (metadata with { MappingLength = metadata.MappingLength + 1 }).Validate());
        Assert.Throws<TransportProtocolException>(() => (metadata with { ByteLength = metadata.ByteLength + 1 }).Validate());
    }

    [Fact]
    public async Task CodecRejectsUnsupportedVersionBadSecretsAndTruncatedFrames()
    {
        var codec = new LengthPrefixedFrameCodec();
        var unsupported = CreateEnvelopeFrame("null", """{"major":2,"minor":0}""");
        await Assert.ThrowsAsync<TransportProtocolException>(async () => await codec.ReadAsync(new MemoryStream(unsupported)));

        var badSecret = CreateEnvelopeFrame("null", secretJson: "\"AQ==\"");
        await Assert.ThrowsAsync<TransportProtocolException>(async () => await codec.ReadAsync(new MemoryStream(badSecret)));

        var valid = CreateEnvelopeFrame("null");
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await codec.ReadAsync(new MemoryStream(valid[..^1])));
    }

    [Fact]
    public async Task LeaseTransitionsAreExactlyOnceAndCrashCleanupReclaimsThem()
    {
        var session = Guid.NewGuid();
        var registry = new SharedMemoryLeaseRegistry(TimeSpan.FromMinutes(1));
        var lease = NewLease(registry, session, Guid.NewGuid());
        Assert.Equal(SharedMemoryLeaseState.Acquired, lease.State);
        Assert.True(lease.Acknowledge());
        Assert.Equal(SharedMemoryLeaseState.Acknowledged, lease.State);
        Assert.False(lease.Acknowledge());
        Assert.True(lease.Release());
        Assert.False(lease.Release());
        Assert.Equal(0, registry.Count);

        var orphan = NewLease(registry, session, Guid.NewGuid());
        Assert.True(registry.Contains(orphan.LeaseId));
        Assert.Equal(1, registry.ReclaimAll(session));
        Assert.False(registry.Contains(orphan.LeaseId));
        await orphan.DisposeAsync();
    }

    private static byte[] CreateEncodedFrame(string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var frame = new byte[sizeof(int) + body.Length];
        BitConverter.GetBytes(body.Length).CopyTo(frame, 0);
        body.CopyTo(frame, sizeof(int));
        return frame;
    }

    private static byte[] CreateEnvelopeFrame(string payloadJson, string? versionJson = null, string? secretJson = null)
    {
        versionJson ??= """{"major":1,"minor":0}""";
        secretJson ??= "\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"";
        return CreateEncodedFrame($$"""{"version":{{versionJson}},"secret":{{secretJson}},"kind":0,"correlationId":"9c52a4f9-3f2e-4f45-b5f0-c79ea886c6a5","identity":{"sessionId":"7d1fbfd1-42e4-4a2e-b749-fd6726080da8"},"payload":{{payloadJson}}}""");
    }

    private static SharedMemoryLeaseMetadata NewLeaseMetadata(Guid session, Guid id)
    {
        var key = new RenderKey(DocumentId.New(), PageId.New(), PageContentRevision.Initial, PageAppearanceRevision.Initial,
            new TileAddress(0, 0, 2, 2, 1), new RasterScale64(64), PageRotation.None, RenderMode.Normal);
        return new SharedMemoryLeaseMetadata
        {
            LeaseId = id,
            SessionId = session,
            SharedMemoryId = "map-1",
            MappingLength = 64,
            Offset = 0,
            ByteLength = 32,
            Width = 2,
            Height = 2,
            Stride = 8,
            Key = key
        };
    }

    private static SharedMemoryLease NewLease(SharedMemoryLeaseRegistry registry, Guid session, Guid id)
    {
        var metadata = NewLeaseMetadata(session, id);
        Assert.True(registry.TryAcquire(metadata, out var lease));
        return lease;
    }
}
