using System.IO.Pipelines;
using System.Text.Json;
using ElliePdf.Pdf.Transport;

namespace ElliePdf.Pdfium.Worker.Tests;

public sealed class PdfWorkerServerTests
{
    [Fact(Timeout = 10_000)]
    public async Task Heartbeat_round_trips_in_memory()
    {
        var sessionId = Guid.NewGuid();
        var secret = LaunchSecret.Generate();
        await using var harness = await ServerHarness.StartAsync(sessionId, secret);
        var correlationId = Guid.NewGuid();

        await harness.WriteAsync(TransportEnvelope.Create(
            TransportMessageKind.Heartbeat,
            secret.ToArray(),
            TransportIdentity.ForSession(sessionId),
            new HeartbeatMessage(DateTimeOffset.UtcNow, 7),
            TransportJsonContext.Default.HeartbeatMessage,
            correlationId));

        var response = await harness.ReadAsync();
        var heartbeat = response.Payload.Deserialize(TransportJsonContext.Default.HeartbeatMessage);

        Assert.Equal(TransportMessageKind.Heartbeat, response.Kind);
        Assert.Equal(correlationId, response.CorrelationId);
        Assert.NotNull(heartbeat);
        Assert.Equal(7, heartbeat!.Sequence);
    }

    [Fact(Timeout = 10_000)]
    public async Task Invalid_secret_fails_authentication_and_terminates_the_server()
    {
        var sessionId = Guid.NewGuid();
        var secret = LaunchSecret.Generate();
        var otherSecret = LaunchSecret.Generate();
        await using var harness = await ServerHarness.StartAsync(sessionId, secret);

        await harness.WriteAsync(TransportEnvelope.Create(
            TransportMessageKind.Heartbeat,
            otherSecret.ToArray(),
            TransportIdentity.ForSession(sessionId),
            new HeartbeatMessage(DateTimeOffset.UtcNow, 1),
            TransportJsonContext.Default.HeartbeatMessage));

        await harness.CompleteClientAsync();
        var exception = await Assert.ThrowsAsync<TransportProtocolException>(() => harness.Completion);
        Assert.Equal("Authentication failed.", exception.Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task Malformed_request_returns_protocol_error()
    {
        var sessionId = Guid.NewGuid();
        var secret = LaunchSecret.Generate();
        await using var harness = await ServerHarness.StartAsync(sessionId, secret);
        using var payload = JsonDocument.Parse("{}");

        await harness.WriteAsync(TransportEnvelope.Create(
            TransportMessageKind.Request,
            secret.ToArray(),
            TransportIdentity.ForSession(sessionId),
            payload.RootElement.Clone()));

        var response = await harness.ReadAsync();
        var error = response.Payload.Deserialize(TransportJsonContext.Default.TransportError);

        Assert.Equal(TransportMessageKind.Error, response.Kind);
        Assert.NotNull(error);
        Assert.Equal("protocol_error", error!.Code);
        Assert.Equal("The worker rejected an invalid protocol message.", error.Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task Cancel_for_unknown_correlation_returns_negative_acknowledgement()
    {
        var sessionId = Guid.NewGuid();
        var secret = LaunchSecret.Generate();
        await using var harness = await ServerHarness.StartAsync(sessionId, secret);
        var correlationId = Guid.NewGuid();

        await harness.WriteAsync(TransportEnvelope.Create(
            TransportMessageKind.Cancel,
            secret.ToArray(),
            TransportIdentity.ForSession(sessionId),
            new CancelMessage(correlationId, "test"),
            TransportJsonContext.Default.CancelMessage,
            correlationId));

        var response = await harness.ReadAsync();
        var ack = response.Payload.Deserialize(WorkerProtocolJsonContext.Default.AcknowledgementResponse);

        Assert.Equal(TransportMessageKind.Response, response.Kind);
        Assert.Equal(correlationId, response.CorrelationId);
        Assert.NotNull(ack);
        Assert.False(ack!.Accepted);
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly Stream _serverStream;
        private readonly Stream _clientStream;
        private readonly PdfWorkerServer _server;
        private readonly LengthPrefixedFrameCodec _codec = new();

        private ServerHarness(Stream serverStream, Stream clientStream, PdfWorkerServer server, Task completion)
        {
            _serverStream = serverStream;
            _clientStream = clientStream;
            _server = server;
            Completion = completion;
        }

        public Task Completion { get; }

        public static Task<ServerHarness> StartAsync(Guid sessionId, LaunchSecret secret)
        {
            var (serverStream, clientStream) = InMemoryDuplexStream.CreatePair();
            var server = new PdfWorkerServer(sessionId, secret, AppContext.BaseDirectory);
            var completion = server.RunAsync(serverStream);
            return Task.FromResult(new ServerHarness(serverStream, clientStream, server, completion));
        }

        public ValueTask WriteAsync(TransportEnvelope envelope)
            => _codec.WriteAsync(_clientStream, envelope);

        public async Task<TransportEnvelope> ReadAsync()
            => (await _codec.ReadAsync(_clientStream))!;

        public ValueTask CompleteClientAsync() => _clientStream.DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            await _clientStream.DisposeAsync();
            try
            {
                await Completion;
            }
            catch (TransportProtocolException)
            {
            }

            await _server.DisposeAsync();
            await _serverStream.DisposeAsync();
        }
    }

    private sealed class InMemoryDuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _readStream = reader.AsStream();
        private readonly Stream _writeStream = writer.AsStream();

        public static (Stream Server, Stream Client) CreatePair()
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            return (
                new InMemoryDuplexStream(clientToServer.Reader, serverToClient.Writer),
                new InMemoryDuplexStream(serverToClient.Reader, clientToServer.Writer));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _writeStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _writeStream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _readStream.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _readStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _readStream.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _writeStream.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _writeStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _writeStream.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            await _writeStream.DisposeAsync();
            await _readStream.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
