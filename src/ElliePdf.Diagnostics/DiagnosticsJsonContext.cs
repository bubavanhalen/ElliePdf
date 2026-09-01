using System.Text.Json.Serialization;

namespace ElliePdf.Diagnostics;

internal sealed record SupportBundleDocument(
    int Schema,
    DateTimeOffset GeneratedAt,
    string[] Events);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(
    typeof(Dictionary<string, object?>),
    TypeInfoPropertyName = "DiagnosticProperties")]
[JsonSerializable(
    typeof(SupportBundleDocument),
    TypeInfoPropertyName = "SupportBundleDocument")]
internal partial class DiagnosticsJsonContext : JsonSerializerContext;
