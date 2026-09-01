using System.Text.Json;

namespace ElliePdf.Benchmark;

internal sealed record ComparatorManifest(
    string SchemaVersion,
    string StatisticalMethod,
    int MinimumIterations,
    string PowerMode,
    string MachineClass,
    string CacheClearingProcedure,
    IReadOnlyList<ComparatorDefinition> Comparators);

internal sealed record ComparatorDefinition(
    string Name,
    string ExactVersion,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<string> CorpusHashes,
    IReadOnlyDictionary<string, double>? BaselineP95);

internal static class ComparatorManifestLoader
{
    public static async Task<ComparatorManifest> LoadAndValidateAsync(string path, bool requireRecordedValues = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Comparator manifest was not found.", path);
        var value = JsonSerializer.Deserialize<ComparatorManifest>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("Invalid comparator manifest.");
        if (value.SchemaVersion != "1.0" || value.MinimumIterations < 30 || string.IsNullOrWhiteSpace(value.StatisticalMethod))
            throw new ArgumentException("Comparator manifest must declare schema 1.0, statistical method and at least 30 iterations.");
        var required = new[] { "Adobe Acrobat Reader", "Microsoft Edge PDF viewer", "SumatraPDF" };
        if (value.Comparators.Count != required.Length || required.Any(name => !value.Comparators.Any(c => c.Name == name)))
            throw new ArgumentException("Comparator manifest must freeze Acrobat, Edge PDF viewer and SumatraPDF exactly once.");
        if (value.Comparators.Any(c => string.IsNullOrWhiteSpace(c.ExactVersion) || c.Settings is null || c.CorpusHashes is null))
            throw new ArgumentException("Every comparator must declare an exact version and corpus hashes.");
        if (requireRecordedValues)
        {
            if (IsPlaceholder(value.PowerMode) || IsPlaceholder(value.MachineClass) || IsPlaceholder(value.CacheClearingProcedure))
                throw new ArgumentException("Comparator manifest still contains required run-condition placeholders; record the frozen reference-machine values before a release benchmark.");
            foreach (var comparator in value.Comparators)
            {
                if (IsPlaceholder(comparator.ExactVersion) || comparator.Settings.Count == 0 || comparator.CorpusHashes.Count == 0 ||
                    comparator.CorpusHashes.Any(IsPlaceholder) || comparator.BaselineP95 is null ||
                    comparator.BaselineP95.Any(pair => pair.Value <= 0 || !double.IsFinite(pair.Value)))
                    throw new ArgumentException($"Comparator '{comparator.Name}' is not frozen: exact version, settings, corpus hashes and valid p95 baselines are required for a release benchmark.");
            }
        }
        return value;
    }

    public static void ValidateScenarioEvidence(
        ComparatorManifest manifest,
        IReadOnlyCollection<string> requiredMetricNames,
        string fixtureSha256)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(requiredMetricNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureSha256);
        foreach (ComparatorDefinition comparator in manifest.Comparators)
        {
            if (!comparator.CorpusHashes.Contains(fixtureSha256, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Comparator '{comparator.Name}' has no measurement for the selected corpus hash.");
            foreach (string metricName in requiredMetricNames)
            {
                if (comparator.BaselineP95 is null ||
                    !comparator.BaselineP95.TryGetValue(metricName, out double value) ||
                    value <= 0 || !double.IsFinite(value))
                    throw new ArgumentException($"Comparator '{comparator.Name}' has no valid p95 baseline for '{metricName}'.");
            }
        }
    }

    private static bool IsPlaceholder(string value) => value.StartsWith("RECORD_", StringComparison.OrdinalIgnoreCase);
}
