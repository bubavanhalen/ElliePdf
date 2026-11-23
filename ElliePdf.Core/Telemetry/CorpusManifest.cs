using System.Security.Cryptography;
using System.Text.Json;

namespace ElliePdf.Telemetry;

public sealed record CorpusFixture(string Id, string Kind, int Pages, string Sha256)
{
    /// <summary>Repository-relative fixture filename. Kept separate from the opaque ID used in reports.</summary>
    public string? File { get; init; }
    /// <summary>Optional exact byte size for generated stress fixtures.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Human-readable deterministic generation recipe; never telemetry payload.</summary>
    public string? Generation { get; init; }
}
public sealed record CorpusManifest(string SchemaVersion, string LicensePolicy, string HashAlgorithm, IReadOnlyList<CorpusFixture> Fixtures)
{
    public static CorpusManifest Load(string json) => JsonSerializer.Deserialize<CorpusManifest>(json, JsonOptions) ?? throw new JsonException("Invalid corpus manifest.");

    public async Task<bool> VerifyFileAsync(CorpusFixture fixture, string path, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(HashAlgorithm, "SHA-256", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException($"Unsupported hash algorithm: {HashAlgorithm}");
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(fixture.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
