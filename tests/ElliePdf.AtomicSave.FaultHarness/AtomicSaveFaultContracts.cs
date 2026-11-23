using System.Text.Json.Serialization;

namespace ElliePdf.AtomicSave.FaultHarness;

public sealed record AtomicSaveFaultRunOptions(
    int Iterations,
    int Seed,
    string ReportPath,
    int Parallelism,
    int PayloadBytes,
    string Configuration,
    bool RetainSuccessfulArtifacts = false,
    TimeSpan? ChildTimeout = null)
{
    public AtomicSaveFaultRunOptions Validate(int stageCount)
    {
        if (Iterations < stageCount || Iterations > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Iterations),
                $"Iterations must be between {stageCount} and 1,000,000 so every stage is covered.");
        }

        if (Parallelism is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism));
        }

        if (PayloadBytes is < 512 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(PayloadBytes));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ReportPath);
        if (!Path.IsPathFullyQualified(ReportPath))
        {
            throw new ArgumentException("The report path must be absolute.", nameof(ReportPath));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Configuration);
        if (Configuration.Length > 64
            || Configuration.Any(static character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                "Configuration must be a privacy-safe ASCII identifier.",
                nameof(Configuration));
        }

        var childTimeout = ChildTimeout ?? TimeSpan.FromSeconds(30);
        if (childTimeout < TimeSpan.FromSeconds(1) || childTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ChildTimeout));
        }

        return this with { ChildTimeout = childTimeout };
    }
}

public sealed record AtomicSaveFaultRunResult(
    AtomicSaveFaultReport Report,
    string WorkRoot);

public sealed record AtomicSaveFaultReport(
    int SchemaVersion,
    string Suite,
    string RunId,
    string Result,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    string Configuration,
    int Seed,
    int IterationsRequested,
    int IterationsCompleted,
    int PayloadBytes,
    int Parallelism,
    string TerminationMode,
    AtomicSaveFaultPolicy Policy,
    AtomicSaveFaultTotals Totals,
    AtomicSaveFaultInvariants Invariants,
    IReadOnlyList<AtomicSaveFaultStageSummary> StageCoverage,
    IReadOnlyList<AtomicSaveFaultCaseEvidence> Cases,
    IReadOnlyList<AtomicSaveFaultFailure> Failures);

public sealed record AtomicSaveFaultPolicy(
    bool RequiresCompleteOldOrNewDestination,
    bool RequiresActualChildTermination,
    bool RequiresEveryStageCovered,
    bool AllowsOutcomeUnknown,
    bool IncludesUserDocumentData);

public sealed record AtomicSaveFaultTotals(
    int Passed,
    int Failed,
    int OldOutcomes,
    int NewOutcomes,
    int RetainedFailureArtifacts);

public sealed record AtomicSaveFaultInvariants(
    int InvalidDestinationCount,
    int MissingDestinationCount,
    int BoundaryNotReachedCount,
    int ChildTerminationFailureCount,
    int JournalParseFailureCount,
    int OutcomeUnknownCount);

public sealed record AtomicSaveFaultStageSummary(
    string Stage,
    int Iterations,
    int Passed,
    int Failed,
    int OldOutcomes,
    int NewOutcomes,
    int InvalidOutcomes);

public sealed record AtomicSaveFaultCaseEvidence(
    int Iteration,
    string Stage,
    string ArtifactId,
    bool Passed,
    bool BoundaryReached,
    bool TimedOut,
    int? ChildExitCode,
    string Outcome,
    int? DestinationLength,
    string? DestinationSha256,
    string OldSha256,
    string NewSha256,
    IReadOnlyList<string> JournalStages,
    bool JournalPresent,
    bool TemporaryPresent,
    bool BackupPresent,
    bool DisplacedPresent,
    bool JournalParseFailed,
    long DurationMilliseconds,
    string? FailureCode);

public sealed record AtomicSaveFaultFailure(
    int Iteration,
    string Stage,
    string ArtifactId,
    string FailureCode);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(AtomicSaveFaultReport))]
internal partial class AtomicSaveFaultJsonContext : JsonSerializerContext;
