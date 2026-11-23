namespace ElliePdf.Pdf.Transport;

/// <summary>Authenticates and rejects identities that are from an earlier document generation.</summary>
public sealed class TransportIdentityValidator
{
    private readonly object _gate = new();
    private readonly LaunchSecret _secret;
    private readonly Guid _sessionId;
    private readonly Dictionary<Guid, IdentityWatermark> _documents = [];
    private readonly Dictionary<(Guid DocumentId, Guid PageId), PageIdentityWatermark> _pages = [];

    public TransportIdentityValidator(Guid sessionId, LaunchSecret secret)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("The session id must not be empty.", nameof(sessionId));
        _sessionId = sessionId;
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
    }

    public Guid SessionId => _sessionId;

    public void Validate(TransportEnvelope envelope, bool updateWatermark = false)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.Validate(TransportProtocolVersion.V1);
        if (!_secret.Matches(envelope.Secret)) throw new TransportProtocolException("Authentication failed.");
        var identity = envelope.Identity;
        if (identity.SessionId != _sessionId) throw new TransportProtocolException("The session identity is stale or invalid.");
        if (identity.DocumentId is not Guid documentId) return;

        lock (_gate)
        {
            if (!_documents.TryGetValue(documentId, out var current))
            {
                if (updateWatermark) _documents[documentId] = IdentityWatermark.From(identity);
                ValidatePageWatermark(documentId, identity, updateWatermark);
                return;
            }
            if (current.IsStale(identity)) throw new TransportProtocolException("The document identity is stale.");
            if (updateWatermark) _documents[documentId] = current.Merge(identity);

            ValidatePageWatermark(documentId, identity, updateWatermark);
        }
    }

    public void ForgetDocument(Guid documentId)
    {
        lock (_gate)
        {
            _documents.Remove(documentId);
            foreach (var key in _pages.Keys.Where(key => key.DocumentId == documentId).ToArray())
                _pages.Remove(key);
        }
    }

    private void ValidatePageWatermark(Guid documentId, TransportIdentity identity, bool updateWatermark)
    {
        if (identity.PageId is not Guid pageId)
        {
            return;
        }

        var key = (documentId, pageId);
        if (!_pages.TryGetValue(key, out var current))
        {
            if (updateWatermark) _pages[key] = PageIdentityWatermark.From(identity);
            return;
        }

        if (current.IsStale(identity))
            throw new TransportProtocolException("The page identity is stale.");
        if (updateWatermark) _pages[key] = current.Merge(identity);
    }

    private readonly record struct IdentityWatermark(
        long? ContentRevision,
        long? StructureRevision)
    {
        public static IdentityWatermark From(TransportIdentity x) => new(x.ContentRevision, x.StructureRevision);
        public bool IsStale(TransportIdentity x) =>
            Less(x.ContentRevision, ContentRevision) || Less(x.StructureRevision, StructureRevision);
        public IdentityWatermark Merge(TransportIdentity x) => new(
            Max(ContentRevision, x.ContentRevision),
            Max(StructureRevision, x.StructureRevision));
        private static bool Less(long? candidate, long? current) => candidate.HasValue && current.HasValue && candidate.Value < current.Value;
        private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    }

    private readonly record struct PageIdentityWatermark(
        long? ContentRevision,
        long? AppearanceRevision,
        long? RenderGeneration,
        long? SearchGeneration)
    {
        public static PageIdentityWatermark From(TransportIdentity x) => new(
            x.PageContentRevision,
            x.PageAppearanceRevision,
            x.RenderGeneration,
            x.SearchGeneration);
        public bool IsStale(TransportIdentity x) =>
            Less(x.PageContentRevision, ContentRevision)
            || Less(x.PageAppearanceRevision, AppearanceRevision)
            || Less(x.RenderGeneration, RenderGeneration)
            || Less(x.SearchGeneration, SearchGeneration);
        public PageIdentityWatermark Merge(TransportIdentity x) => new(
            Max(ContentRevision, x.PageContentRevision),
            Max(AppearanceRevision, x.PageAppearanceRevision),
            Max(RenderGeneration, x.RenderGeneration),
            Max(SearchGeneration, x.SearchGeneration));
        private static bool Less(long? candidate, long? current) => candidate.HasValue && current.HasValue && candidate.Value < current.Value;
        private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    }
}

public static class TransportAuthentication
{
    public static void Validate(TransportEnvelope envelope, LaunchSecret secret, Guid expectedSessionId)
    {
        var validator = new TransportIdentityValidator(expectedSessionId, secret);
        validator.Validate(envelope);
    }
}

/// <summary>Reusable per-launch authenticator; callers invoke it for every received request.</summary>
public sealed class TransportAuthenticator
{
    private readonly TransportIdentityValidator _validator;
    public TransportAuthenticator(Guid sessionId, LaunchSecret secret) => _validator = new TransportIdentityValidator(sessionId, secret);
    public Guid SessionId => _validator.SessionId;
    public void Validate(TransportEnvelope envelope, bool updateWatermark = false) => _validator.Validate(envelope, updateWatermark);
    public void ForgetDocument(Guid documentId) => _validator.ForgetDocument(documentId);
}
