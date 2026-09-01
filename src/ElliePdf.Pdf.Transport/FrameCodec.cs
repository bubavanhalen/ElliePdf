using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace ElliePdf.Pdf.Transport;

public sealed record TransportCodecOptions
{
    public int MaxFrameBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxArrayLength { get; init; } = 100_000;
    public int MaxStringLength { get; init; } = 16 * 1024 * 1024;
    public int MaxDepth { get; init; } = 64;

    internal void Validate()
    {
        if (MaxFrameBytes is <= 0 or > 512 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxFrameBytes));
        if (MaxArrayLength is <= 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaxArrayLength));
        if (MaxStringLength is <= 0 or > 512 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxStringLength));
        if (MaxDepth is <= 0 or > 256) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
    }
}

/// <summary>Little-endian uint32 length prefix followed by one UTF-8 v1 envelope.</summary>
public sealed class LengthPrefixedFrameCodec
{
    public TransportCodecOptions Options { get; }
    public LengthPrefixedFrameCodec(TransportCodecOptions? options = null)
    {
        Options = options ?? new TransportCodecOptions();
        Options.Validate();
    }

    public async ValueTask WriteAsync(Stream stream, TransportEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.Validate(TransportProtocolVersion.V1);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, TransportJsonContext.Default.TransportEnvelope);
        ValidateJson(body);
        if (body.Length > Options.MaxFrameBytes) throw new TransportProtocolException("Frame exceeds the configured maximum.");
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask WriteEnvelopeAsync(Stream stream, TransportEnvelope envelope, CancellationToken cancellationToken = default)
        => WriteAsync(stream, envelope, cancellationToken);

    /// <returns>null only when EOF occurs before the first prefix byte.</returns>
    public async ValueTask<TransportEnvelope?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[sizeof(int)];
        var first = await stream.ReadAsync(prefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await ReadExactlyAsync(stream, prefix.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > Options.MaxFrameBytes) throw new TransportProtocolException("Invalid frame length.");
        var body = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(stream, body.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            ValidateJson(body.AsSpan(0, length));
            var envelope = JsonSerializer.Deserialize(body.AsSpan(0, length), TransportJsonContext.Default.TransportEnvelope)
                ?? throw new TransportProtocolException("Frame contained no envelope.");
            envelope.Validate(TransportProtocolVersion.V1);
            return envelope;
        }
        catch (JsonException ex)
        {
            throw new TransportProtocolException("Malformed JSON frame.", ex);
        }
        finally { ArrayPool<byte>.Shared.Return(body); }
    }

    public ValueTask<TransportEnvelope?> ReadEnvelopeAsync(Stream stream, CancellationToken cancellationToken = default)
        => ReadAsync(stream, cancellationToken);

    private void ValidateJson(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = Options.MaxDepth, CommentHandling = JsonCommentHandling.Disallow });
            var arrays = new Stack<(int Depth, int Count)>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.String && reader.GetString()!.Length > Options.MaxStringLength)
                    throw new TransportProtocolException("String exceeds the configured maximum.");

                if (arrays.Count > 0
                    && reader.CurrentDepth == arrays.Peek().Depth + 1
                    && reader.TokenType is JsonTokenType.StartArray
                        or JsonTokenType.StartObject
                        or JsonTokenType.String
                        or JsonTokenType.Number
                        or JsonTokenType.True
                        or JsonTokenType.False
                        or JsonTokenType.Null)
                {
                    var current = arrays.Pop();
                    var count = current.Count + 1;
                    if (count > Options.MaxArrayLength) throw new TransportProtocolException("Array exceeds the configured maximum.");
                    arrays.Push((current.Depth, count));
                }

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    arrays.Push((reader.CurrentDepth, 0));
                }
                else if (reader.TokenType == JsonTokenType.EndArray)
                {
                    if (arrays.Count == 0 || arrays.Peek().Depth != reader.CurrentDepth)
                        throw new TransportProtocolException("Malformed array nesting.");
                    arrays.Pop();
                }
            }
            if (reader.BytesConsumed != body.Length) throw new TransportProtocolException("Trailing data in frame.");
        }
        catch (JsonException ex) { throw new TransportProtocolException("Malformed JSON frame.", ex); }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Unexpected EOF in a length-prefixed frame.");
            read += n;
        }
    }
}
