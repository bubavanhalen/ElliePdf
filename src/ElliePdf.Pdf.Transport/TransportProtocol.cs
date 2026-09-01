using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Pdf.Transport;

public readonly record struct TransportProtocolVersion(int Major, int Minor)
{
    public static TransportProtocolVersion V1 => new(1, 0);
    public TransportProtocolVersion Validate()
    {
        if (Major <= 0 || Minor < 0) throw new TransportProtocolException("Invalid protocol version.");
        return this;
    }
}

public enum TransportMessageKind
{
    Request,
    Response,
    Error,
    Cancel,
    Heartbeat,
    LeaseAcquire,
    LeaseAck,
    LeaseRelease,
    LeaseAcknowledgement = LeaseAck,
    LeaseAcknowledge = LeaseAck
}

public sealed class TransportProtocolException : IOException
{
    public TransportProtocolException(string message) : base(message) { }
    public TransportProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>All identity values which can make a worker result stale.</summary>
public sealed record TransportIdentity
{
    public Guid SessionId { get; init; }
    public Guid? DocumentId { get; init; }
    public Guid? PageId { get; init; }
    public long? ContentRevision { get; init; }
    public long? PageContentRevision { get; init; }
    public long? PageAppearanceRevision { get; init; }
    public long? StructureRevision { get; init; }
    public long? RenderGeneration { get; init; }
    public long? SearchGeneration { get; init; }

    public static TransportIdentity ForSession(Guid sessionId) => new() { SessionId = sessionId };
    public static TransportIdentity ForDocument(Guid sessionId, DocumentId documentId, ContentRevision revision) => new()
    {
        SessionId = sessionId, DocumentId = documentId.Value, ContentRevision = revision.Value
    };
    public static TransportIdentity ForPage(Guid sessionId, DocumentId documentId, PageId pageId, PageContentRevision revision) => new()
    {
        SessionId = sessionId, DocumentId = documentId.Value, PageId = pageId.Value, PageContentRevision = revision.Value
    };
    public static TransportIdentity ForRender(Guid sessionId, RenderKey key, RenderGeneration generation) => new()
    {
        SessionId = sessionId, DocumentId = key.DocumentId.Value, PageId = key.PageId.Value,
        PageContentRevision = key.ContentRevision.Value, PageAppearanceRevision = key.AppearanceRevision.Value,
        RenderGeneration = generation.Value
    };
    public static TransportIdentity ForSearch(Guid sessionId, DocumentId documentId, PageId pageId, PageContentRevision revision, SearchGeneration generation) => new()
    {
        SessionId = sessionId, DocumentId = documentId.Value, PageId = pageId.Value,
        PageContentRevision = revision.Value, SearchGeneration = generation.Value
    };

    internal void Validate()
    {
        if (SessionId == Guid.Empty) throw new TransportProtocolException("A session identity is required.");
        if (DocumentId is Guid document && document == Guid.Empty) throw new TransportProtocolException("Document identity must not be empty.");
        if (PageId is Guid page && page == Guid.Empty) throw new TransportProtocolException("Page identity must not be empty.");
        if (ContentRevision is < 0 || PageContentRevision is < 0 || PageAppearanceRevision is < 0 || StructureRevision is < 0 || RenderGeneration is < 0 || SearchGeneration is < 0)
            throw new TransportProtocolException("Identity revisions and generations must not be negative.");
    }
}

public sealed record TransportEnvelope
{
    public TransportProtocolVersion Version { get; init; } = TransportProtocolVersion.V1;
    public byte[] Secret { get; init; } = [];
    public TransportMessageKind Kind { get; init; }
    public Guid CorrelationId { get; init; }
    public TransportIdentity Identity { get; init; } = new();
    public JsonElement Payload { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    [JsonIgnore]
    public TransportProtocolVersion ProtocolVersion => Version;
    [JsonIgnore]
    public TransportMessageKind MessageKind => Kind;

    public static TransportEnvelope Create(TransportMessageKind kind, ReadOnlySpan<byte> secret, TransportIdentity identity, JsonElement payload, Guid? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (secret.Length != LaunchSecret.ByteLength) throw new ArgumentException("The launch secret must be exactly 256 bits.", nameof(secret));
        var copy = secret.ToArray();
        var result = new TransportEnvelope { Kind = kind, Secret = copy, Identity = identity, Payload = payload, CorrelationId = correlationId ?? Guid.NewGuid() };
        result.Validate(TransportProtocolVersion.V1);
        return result;
    }

    /// <summary>Creates a payload using caller-supplied source-generated metadata (Native AOT safe).</summary>
    public static TransportEnvelope Create<T>(TransportMessageKind kind, ReadOnlySpan<byte> secret, TransportIdentity identity, T payload, JsonTypeInfo<T> payloadTypeInfo, Guid? correlationId = null, DateTimeOffset? deadlineUtc = null)
    {
        ArgumentNullException.ThrowIfNull(payloadTypeInfo);
        var element = JsonSerializer.SerializeToElement(payload, payloadTypeInfo);
        var envelope = Create(kind, secret, identity, element, correlationId);
        return envelope with { DeadlineUtc = deadlineUtc };
    }

    public void Validate(TransportProtocolVersion expectedVersion)
    {
        if (Version != expectedVersion) throw new TransportProtocolException($"Unsupported protocol version {Version.Major}.{Version.Minor}.");
        if (Secret is null || Secret.Length != LaunchSecret.ByteLength) throw new TransportProtocolException("Missing or invalid launch secret.");
        if (CorrelationId == Guid.Empty) throw new TransportProtocolException("Correlation identity must not be empty.");
        (Identity ?? throw new TransportProtocolException("Session identity is required.")).Validate();
        if (Payload.ValueKind == JsonValueKind.Undefined) throw new TransportProtocolException("A payload is required.");
    }
}

public sealed class LaunchSecret
{
    public const int ByteLength = 32;
    private readonly byte[] _bytes;
    public LaunchSecret(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength) throw new ArgumentException("A launch secret must be exactly 256 bits.", nameof(bytes));
        _bytes = bytes.ToArray();
    }
    public static LaunchSecret Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(ByteLength);
        return new LaunchSecret(bytes);
    }
    public byte[] ToArray() => _bytes.ToArray();
    public ReadOnlyMemory<byte> Bytes => _bytes;
    public bool Matches(ReadOnlySpan<byte> candidate) => candidate.Length == ByteLength && CryptographicOperations.FixedTimeEquals(_bytes, candidate);
}

public sealed record TransportError(string Code, string Message, bool IsTransient = false)
{
    public TransportError Validate()
    {
        if (string.IsNullOrWhiteSpace(Code) || Code.Length > PdfContractLimits.MaxStringLength) throw new TransportProtocolException("Invalid error code.");
        if (string.IsNullOrWhiteSpace(Message) || Message.Length > PdfContractLimits.MaxStringLength) throw new TransportProtocolException("Invalid error message.");
        return this;
    }
}

public sealed record CancelMessage(Guid TargetCorrelationId, string? Reason = null)
{
    public CancelMessage Validate()
    {
        if (TargetCorrelationId == Guid.Empty)
        {
            throw new TransportProtocolException("A cancellation target is required.");
        }

        if (Reason is { Length: > PdfContractLimits.MaxStringLength })
        {
            throw new TransportProtocolException("The cancellation reason is too long.");
        }

        return this;
    }
}
public sealed record HeartbeatMessage(DateTimeOffset AtUtc, long Sequence);
public sealed record LeaseAcquireMessage(SharedMemoryLeaseMetadata Metadata);
public sealed record LeaseAckMessage(Guid LeaseId);
public sealed record LeaseReleaseMessage(Guid LeaseId);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(TransportEnvelope))]
[JsonSerializable(typeof(TransportIdentity))]
[JsonSerializable(typeof(TransportProtocolVersion))]
[JsonSerializable(typeof(TransportError))]
[JsonSerializable(typeof(CancelMessage))]
[JsonSerializable(typeof(HeartbeatMessage))]
[JsonSerializable(typeof(LeaseAcquireMessage))]
[JsonSerializable(typeof(LeaseAckMessage))]
[JsonSerializable(typeof(LeaseReleaseMessage))]
[JsonSerializable(typeof(BrokeredHandleDescriptor))]
public partial class TransportJsonContext : JsonSerializerContext;
