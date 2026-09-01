namespace ElliePdf.Domain.Storage;

public sealed record FileVersionStamp(
    string CanonicalPath,
    string? FileIdentity,
    long Length,
    DateTimeOffset LastWriteUtc,
    string Sha256)
{
    public bool Matches(FileVersionStamp? other) =>
        other is not null
        && string.Equals(CanonicalPath, other.CanonicalPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(FileIdentity, other.FileIdentity, StringComparison.Ordinal)
        && Length == other.Length
        && LastWriteUtc == other.LastWriteUtc
        && string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);

    public bool ContentMatches(FileVersionStamp? other) =>
        other is not null
        && Length == other.Length
        && string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);

    public bool IdentifiesSameFile(FileVersionStamp? other) =>
        other is not null
        && (FileIdentity is not null && other.FileIdentity is not null
            ? string.Equals(FileIdentity, other.FileIdentity, StringComparison.Ordinal)
            : string.Equals(CanonicalPath, other.CanonicalPath, StringComparison.OrdinalIgnoreCase));
}
