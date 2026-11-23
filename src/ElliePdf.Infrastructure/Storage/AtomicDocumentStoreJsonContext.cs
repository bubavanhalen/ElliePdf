using System.Text.Json.Serialization;

namespace ElliePdf.Infrastructure.Storage;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(
    typeof(AtomicDocumentStore.AtomicJournal),
    TypeInfoPropertyName = "AtomicJournal")]
internal partial class AtomicDocumentStoreJsonContext : JsonSerializerContext;
