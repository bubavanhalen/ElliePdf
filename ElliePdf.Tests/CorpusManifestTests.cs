using System.Security.Cryptography;
using ElliePdf.Telemetry;
using Xunit;

namespace ElliePdf.Tests;

public sealed class CorpusManifestTests
{
    [Fact]
    public async Task HashValidationDetectsTampering()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "deterministic synthetic fixture");
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var fixture = new CorpusFixture("fixture", "synthetic", 1, hash);
            var manifest = new CorpusManifest("1.0", "synthetic only", "SHA-256", [fixture]);
            Assert.True(await manifest.VerifyFileAsync(fixture, path));
            await File.AppendAllTextAsync(path, "tampered");
            Assert.False(await manifest.VerifyFileAsync(fixture, path));
        }
        finally { File.Delete(path); }
    }
}
