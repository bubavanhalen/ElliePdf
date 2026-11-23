using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElliePdf.Telemetry;

public sealed record BenchmarkMetric(string Name, string Unit, BenchmarkStatistics Statistics);
public sealed record BenchmarkReport(string SchemaVersion, string RunId, string MachineClass, string PowerMode, DateTimeOffset StartedUtc, IReadOnlyList<BenchmarkMetric> Metrics)
{
    /// <summary>Whether the operator ran the documented cold-cache or warm-cache procedure.</summary>
    public string Temperature { get; init; } = "unspecified";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static BenchmarkReport FromJson(string json) => JsonSerializer.Deserialize<BenchmarkReport>(json, JsonOptions) ?? throw new JsonException("Invalid benchmark report.");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}
