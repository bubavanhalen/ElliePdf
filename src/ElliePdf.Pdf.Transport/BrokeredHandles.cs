namespace ElliePdf.Pdf.Transport;

public enum BrokeredHandleAccess
{
    ReadOnlySource,
    TemporaryWrite
}

/// <summary>Opaque authority passed over the protocol. It intentionally has no path or filename.</summary>
public sealed record BrokeredHandleDescriptor
{
    public Guid HandleId { get; init; }
    public Guid SessionId { get; init; }
    public long NativeHandleValue { get; init; }
    public BrokeredHandleAccess Access { get; init; }
    public Guid? TransactionId { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public void Validate(DateTimeOffset? now = null)
    {
        if (HandleId == Guid.Empty || SessionId == Guid.Empty || NativeHandleValue <= 0)
            throw new TransportProtocolException("A brokered handle requires identities and target-process authority.");
        if (Access == BrokeredHandleAccess.TemporaryWrite && (TransactionId is not Guid tx || tx == Guid.Empty))
            throw new TransportProtocolException("Temporary write authority requires a transaction identity.");
        if (Access == BrokeredHandleAccess.ReadOnlySource && TransactionId is not null)
            throw new TransportProtocolException("Read-only source handles cannot have a transaction.");
        if (ExpiresAtUtc is { } expiry && expiry <= (now ?? DateTimeOffset.UtcNow)) throw new TransportProtocolException("Brokered handle authority has expired.");
    }
}
