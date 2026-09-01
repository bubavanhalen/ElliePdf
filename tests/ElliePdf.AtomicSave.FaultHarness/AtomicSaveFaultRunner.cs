using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElliePdf.Domain.Documents;
using ElliePdf.Infrastructure.Storage;

namespace ElliePdf.AtomicSave.FaultHarness;

public static class AtomicSaveFaultRunner
{
    public const int ChildUnexpectedCompletionExitCode = 74;
    private static readonly AtomicSaveStage[] Stages = Enum.GetValues<AtomicSaveStage>();

    public static IReadOnlyList<string> StageNames { get; } =
        Array.AsReadOnly(Stages.Select(static stage => stage.ToString()).ToArray());

    public static async Task<AtomicSaveFaultRunResult> RunAsync(
        AtomicSaveFaultRunOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        options = options.Validate(Stages.Length);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var runId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var workRoot = Path.Combine(
            Path.GetTempPath(),
            "ElliePdf",
            "AtomicSaveFaultHarness",
            runId);
        Directory.CreateDirectory(workRoot);
        ValidateHarnessWorkRoot(workRoot);

        var cases = new AtomicSaveFaultCaseEvidence[options.Iterations];
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Parallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, options.Iterations),
            parallelOptions,
            async (iteration, token) =>
            {
                cases[iteration] = await RunCaseAsync(
                        options,
                        workRoot,
                        iteration,
                        token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

        stopwatch.Stop();
        var orderedCases = Array.AsReadOnly(cases);
        var failures = orderedCases
            .Where(static evidence => !evidence.Passed)
            .Select(static evidence => new AtomicSaveFaultFailure(
                evidence.Iteration,
                evidence.Stage,
                evidence.ArtifactId,
                evidence.FailureCode ?? "unspecified_failure"))
            .ToArray();
        var stageCoverage = Stages
            .Select(stage => BuildStageSummary(stage, orderedCases))
            .ToArray();
        var oldCount = orderedCases.Count(static evidence => evidence.Outcome == "old");
        var newCount = orderedCases.Count(static evidence => evidence.Outcome == "new");
        var invalidCount = orderedCases.Count(static evidence => evidence.Outcome == "invalid");
        var missingCount = orderedCases.Count(static evidence => evidence.Outcome == "missing");
        var boundaryMissingCount = orderedCases.Count(static evidence => !evidence.BoundaryReached);
        var terminationFailureCount = orderedCases.Count(static evidence =>
            evidence.TimedOut
            || evidence.ChildExitCode is null or 0 or ChildUnexpectedCompletionExitCode);
        var journalParseFailureCount = orderedCases.Count(static evidence => evidence.JournalParseFailed);
        var outcomeUnknownCount = orderedCases.Count(static evidence =>
            evidence.JournalStages.Contains("OutcomeUnknown", StringComparer.Ordinal));
        var passed = failures.Length == 0
            && stageCoverage.All(static stage => stage.Iterations > 0)
            && invalidCount == 0
            && missingCount == 0
            && boundaryMissingCount == 0
            && terminationFailureCount == 0
            && journalParseFailureCount == 0
            && outcomeUnknownCount == 0;

        var report = new AtomicSaveFaultReport(
            SchemaVersion: 1,
            Suite: "atomic-save-fault-harness",
            RunId: runId,
            Result: passed ? "pass" : "fail",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            DurationMilliseconds: stopwatch.ElapsedMilliseconds,
            Configuration: options.Configuration,
            Seed: options.Seed,
            IterationsRequested: options.Iterations,
            IterationsCompleted: orderedCases.Count,
            PayloadBytes: options.PayloadBytes,
            Parallelism: options.Parallelism,
            TerminationMode: "child-process-forced-termination",
            Policy: new AtomicSaveFaultPolicy(
                RequiresCompleteOldOrNewDestination: true,
                RequiresActualChildTermination: true,
                RequiresEveryStageCovered: true,
                AllowsOutcomeUnknown: false,
                IncludesUserDocumentData: false),
            Totals: new AtomicSaveFaultTotals(
                Passed: orderedCases.Count(static evidence => evidence.Passed),
                Failed: failures.Length,
                OldOutcomes: oldCount,
                NewOutcomes: newCount,
                RetainedFailureArtifacts: failures.Length),
            Invariants: new AtomicSaveFaultInvariants(
                InvalidDestinationCount: invalidCount,
                MissingDestinationCount: missingCount,
                BoundaryNotReachedCount: boundaryMissingCount,
                ChildTerminationFailureCount: terminationFailureCount,
                JournalParseFailureCount: journalParseFailureCount,
                OutcomeUnknownCount: outcomeUnknownCount),
            StageCoverage: stageCoverage,
            Cases: orderedCases,
            Failures: failures);

        await WriteReportAsync(options.ReportPath, report, cancellationToken).ConfigureAwait(false);
        TryDeleteEmptyRunRoot(workRoot);
        return new AtomicSaveFaultRunResult(report, workRoot);
    }

    internal static async Task<int> RunChildAsync(
        string caseDirectory,
        AtomicSaveStage stage,
        int seed,
        int iteration,
        int payloadBytes)
    {
        EnsureWindows();
        var fullCaseDirectory = ValidateChildDirectory(caseDirectory);
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        if (iteration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iteration));
        }

        if (payloadBytes is < 512 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        }

        var destinationPath = Path.Combine(fullCaseDirectory, "document.pdf");
        if (!File.Exists(destinationPath)
            || (File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The isolated synthetic destination is missing or redirects outside its case directory.");
        }

        var markerPath = Path.Combine(fullCaseDirectory, "boundary.reached");
        var newPayload = CreateSyntheticPayload(seed, iteration, isNew: true, payloadBytes);
        var provider = new FileVersionStampProvider();
        var expectedVersion = await provider.TryCaptureAsync(destinationPath).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The isolated synthetic destination is missing.");
        var observer = new TerminatingStageObserver(stage, markerPath);
        var store = new AtomicDocumentStore(
            provider,
            observer,
            destinationLockProvider: new CrossProcessDestinationLockProvider(
                Path.Combine(fullCaseDirectory, "locks")));

        _ = await store.CommitAsync(
                new AtomicSaveRequest(
                    destinationPath,
                    new ContentRevision(1),
                    expectedVersion),
                async (stream, token) =>
                {
                    await stream.WriteAsync(newPayload, token).ConfigureAwait(false);
                },
                async (candidatePath, token) =>
                {
                    var candidate = await File.ReadAllBytesAsync(candidatePath, token).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(candidate, newPayload))
                    {
                        throw new InvalidDataException("The synthetic prepared payload failed validation.");
                    }
                })
            .ConfigureAwait(false);

        return ChildUnexpectedCompletionExitCode;
    }

    private static async Task<AtomicSaveFaultCaseEvidence> RunCaseAsync(
        AtomicSaveFaultRunOptions options,
        string workRoot,
        int iteration,
        CancellationToken cancellationToken)
    {
        var caseStopwatch = Stopwatch.StartNew();
        var stageIndex = PositiveModulo(iteration + options.Seed, Stages.Length);
        var stage = Stages[stageIndex];
        var artifactId = $"case-{iteration:D6}";
        var caseDirectory = Path.Combine(workRoot, artifactId);
        Directory.CreateDirectory(caseDirectory);
        var destinationPath = Path.Combine(caseDirectory, "document.pdf");
        var oldPayload = CreateSyntheticPayload(options.Seed, iteration, isNew: false, options.PayloadBytes);
        var newPayload = CreateSyntheticPayload(options.Seed, iteration, isNew: true, options.PayloadBytes);
        var oldSha256 = Convert.ToHexString(SHA256.HashData(oldPayload));
        var newSha256 = Convert.ToHexString(SHA256.HashData(newPayload));
        await WriteDurablyAsync(destinationPath, oldPayload, cancellationToken).ConfigureAwait(false);

        int? childExitCode = null;
        var timedOut = false;
        string? failureCode = null;
        try
        {
            using var child = StartChildProcess(
                caseDirectory,
                stage,
                options.Seed,
                iteration,
                options.PayloadBytes);
            try
            {
                var outputTask = child.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = child.StandardError.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.ChildTimeout!.Value);
                try
                {
                    await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    childExitCode = child.ExitCode;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    timedOut = true;
                    failureCode = "child_timeout";
                    TryKill(child);
                    await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    childExitCode = child.ExitCode;
                }

                _ = await outputTask.ConfigureAwait(false);
                _ = await errorTask.ConfigureAwait(false);
            }
            finally
            {
                if (!child.HasExited)
                {
                    TryKill(child);
                    await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failureCode = $"child_{exception.GetType().Name}";
        }

        var boundaryReached = TryReadBoundaryMarker(caseDirectory, stage);
        var destination = await ClassifyDestinationAsync(
                destinationPath,
                oldSha256,
                newSha256,
                cancellationToken)
            .ConfigureAwait(false);
        var journal = CaptureJournalEvidence(caseDirectory);
        var outcomeUnknown = journal.Stages.Contains("OutcomeUnknown", StringComparer.Ordinal);
        var terminated = !timedOut
            && childExitCode is not null and not 0 and not ChildUnexpectedCompletionExitCode;
        var passed = boundaryReached
            && terminated
            && destination.Outcome is "old" or "new"
            && !journal.ParseFailed
            && !outcomeUnknown;
        if (!passed && failureCode is null)
        {
            failureCode = !boundaryReached
                ? "boundary_not_reached"
                : !terminated
                    ? "child_not_force_terminated"
                    : journal.ParseFailed
                        ? "journal_parse_failure"
                        : outcomeUnknown
                            ? "journal_outcome_unknown"
                            : $"destination_{destination.Outcome}";
        }

        caseStopwatch.Stop();
        var evidence = new AtomicSaveFaultCaseEvidence(
            Iteration: iteration,
            Stage: stage.ToString(),
            ArtifactId: artifactId,
            Passed: passed,
            BoundaryReached: boundaryReached,
            TimedOut: timedOut,
            ChildExitCode: childExitCode,
            Outcome: destination.Outcome,
            DestinationLength: destination.Length,
            DestinationSha256: destination.Sha256,
            OldSha256: oldSha256,
            NewSha256: newSha256,
            JournalStages: journal.Stages,
            JournalPresent: journal.Present,
            TemporaryPresent: Directory.EnumerateFiles(caseDirectory, "*.tmp").Any(),
            BackupPresent: Directory.EnumerateFiles(caseDirectory, "*.bak").Any(),
            DisplacedPresent: Directory.EnumerateFiles(caseDirectory, "*.displaced").Any(),
            JournalParseFailed: journal.ParseFailed,
            DurationMilliseconds: caseStopwatch.ElapsedMilliseconds,
            FailureCode: failureCode);

        if (passed && !options.RetainSuccessfulArtifacts)
        {
            TryDeleteCaseDirectory(workRoot, caseDirectory);
        }

        return evidence;
    }

    private static Process StartChildProcess(
        string caseDirectory,
        AtomicSaveStage stage,
        int seed,
        int iteration,
        int payloadBytes)
    {
        var harnessAssembly = typeof(AtomicSaveFaultRunner).Assembly.Location;
        var assemblyDirectory = Path.GetDirectoryName(harnessAssembly)
            ?? throw new InvalidOperationException("The fault harness assembly directory is unavailable.");
        var appHost = Path.Combine(
            assemblyDirectory,
            Path.GetFileNameWithoutExtension(harnessAssembly) + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        var startInfo = new ProcessStartInfo
        {
            FileName = File.Exists(appHost)
                ? appHost
                : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = assemblyDirectory
        };
        if (!File.Exists(appHost))
        {
            startInfo.ArgumentList.Add(harnessAssembly);
        }

        startInfo.ArgumentList.Add("--child");
        startInfo.ArgumentList.Add("--case-directory");
        startInfo.ArgumentList.Add(caseDirectory);
        startInfo.ArgumentList.Add("--stage");
        startInfo.ArgumentList.Add(stage.ToString());
        startInfo.ArgumentList.Add("--seed");
        startInfo.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--iteration");
        startInfo.ArgumentList.Add(iteration.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--payload-bytes");
        startInfo.ArgumentList.Add(payloadBytes.ToString(CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The atomic-save fault child process did not start.");
    }

    private static byte[] CreateSyntheticPayload(int seed, int iteration, bool isNew, int length)
    {
        var payload = new byte[length];
        var label = isNew ? "new" : "old";
        var offset = 0;
        for (var block = 0; offset < payload.Length; block++)
        {
            var material = Encoding.UTF8.GetBytes(
                $"ElliePdf.synthetic.atomic-save.v1:{seed}:{iteration}:{label}:{block}");
            var digest = SHA256.HashData(material);
            var count = Math.Min(digest.Length, payload.Length - offset);
            digest.AsSpan(0, count).CopyTo(payload.AsSpan(offset));
            offset += count;
        }

        return payload;
    }

    private static async Task WriteDurablyAsync(
        string path,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<DestinationEvidence> ClassifyDestinationAsync(
        string destinationPath,
        string oldSha256,
        string newSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(destinationPath))
        {
            return new DestinationEvidence("missing", null, null);
        }

        var bytes = await File.ReadAllBytesAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var outcome = string.Equals(sha256, oldSha256, StringComparison.Ordinal)
            ? "old"
            : string.Equals(sha256, newSha256, StringComparison.Ordinal)
                ? "new"
                : "invalid";
        return new DestinationEvidence(outcome, bytes.Length, sha256);
    }

    private static JournalEvidence CaptureJournalEvidence(string caseDirectory)
    {
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var parseFailed = false;
        var journalFiles = Directory.EnumerateFiles(caseDirectory, "*.journal*").ToArray();
        foreach (var journalFile in journalFiles)
        {
            try
            {
                using var journal = JsonDocument.Parse(File.ReadAllBytes(journalFile));
                if (journal.RootElement.TryGetProperty("stage", out var stage)
                    && TryGetJournalStageName(stage, out var stageName))
                {
                    stages.Add(stageName);
                }
                else
                {
                    parseFailed = true;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                parseFailed = true;
            }
        }

        return new JournalEvidence(
            journalFiles.Length > 0,
            parseFailed,
            stages.Order(StringComparer.Ordinal).ToArray());
    }

    private static bool TryGetJournalStageName(JsonElement stage, out string stageName)
    {
        if (stage.ValueKind == JsonValueKind.String
            && stage.GetString() is { Length: > 0 } text)
        {
            stageName = text;
            return true;
        }

        if (stage.ValueKind == JsonValueKind.Number
            && stage.TryGetInt32(out var value)
            && value is >= 0 and <= 5)
        {
            stageName = value switch
            {
                0 => "Prepared",
                1 => "CommitStarted",
                2 => "Committed",
                3 => "Validated",
                4 => "RolledBack",
                _ => "OutcomeUnknown"
            };
            return true;
        }

        stageName = string.Empty;
        return false;
    }

    private static bool TryReadBoundaryMarker(string caseDirectory, AtomicSaveStage expectedStage)
    {
        try
        {
            var markerPath = Path.Combine(caseDirectory, "boundary.reached");
            return File.Exists(markerPath)
                && string.Equals(
                    File.ReadAllText(markerPath),
                    expectedStage.ToString(),
                    StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static AtomicSaveFaultStageSummary BuildStageSummary(
        AtomicSaveStage stage,
        IReadOnlyCollection<AtomicSaveFaultCaseEvidence> cases)
    {
        var matching = cases.Where(evidence => evidence.Stage == stage.ToString()).ToArray();
        return new AtomicSaveFaultStageSummary(
            Stage: stage.ToString(),
            Iterations: matching.Length,
            Passed: matching.Count(static evidence => evidence.Passed),
            Failed: matching.Count(static evidence => !evidence.Passed),
            OldOutcomes: matching.Count(static evidence => evidence.Outcome == "old"),
            NewOutcomes: matching.Count(static evidence => evidence.Outcome == "new"),
            InvalidOutcomes: matching.Count(static evidence => evidence.Outcome is "invalid" or "missing"));
    }

    private static async Task WriteReportAsync(
        string reportPath,
        AtomicSaveFaultReport report,
        CancellationToken cancellationToken)
    {
        var fullReportPath = Path.GetFullPath(reportPath);
        var reportDirectory = Path.GetDirectoryName(fullReportPath)
            ?? throw new ArgumentException("The report path has no parent directory.", nameof(reportPath));
        Directory.CreateDirectory(reportDirectory);
        var nextPath = fullReportPath + $".next-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                nextPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        report,
                        AtomicSaveFaultJsonContext.Default.AtomicSaveFaultReport,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(nextPath, fullReportPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(nextPath);
        }
    }

    private static string ValidateChildDirectory(string caseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseDirectory);
        var fullPath = Path.GetFullPath(caseDirectory);
        var harnessRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "ElliePdf",
            "AtomicSaveFaultHarness"));
        var runDirectory = Directory.GetParent(fullPath);
        var caseName = Path.GetFileName(fullPath);
        var runName = runDirectory?.Name;
        var harnessParent = Directory.GetParent(harnessRoot);
        if (runDirectory?.Parent is null
            || harnessParent is null
            || !string.Equals(runDirectory.Parent.FullName, harnessRoot, StringComparison.OrdinalIgnoreCase)
            || caseName.Length != "case-000000".Length
            || !caseName.StartsWith("case-", StringComparison.Ordinal)
            || !int.TryParse(caseName.AsSpan("case-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || runName is null
            || !Guid.TryParseExact(runName, "N", out _)
            || !Directory.Exists(fullPath)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(runDirectory.FullName) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(harnessRoot) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(harnessParent.FullName) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Fault children may operate only in a harness-owned isolated temp case directory.");
        }

        return fullPath;
    }

    private static void ValidateHarnessWorkRoot(string workRoot)
    {
        var harnessDirectory = Directory.GetParent(Path.GetFullPath(workRoot));
        var harnessParent = harnessDirectory?.Parent;
        if (harnessDirectory is null
            || harnessParent is null
            || (File.GetAttributes(workRoot) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(harnessDirectory.FullName) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(harnessParent.FullName) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The atomic-save fault work root must not traverse a reparse point.");
        }
    }

    private static int PositiveModulo(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Atomic-save release evidence must execute on Windows.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteCaseDirectory(string workRoot, string caseDirectory)
    {
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workRoot));
            var fullCase = Path.GetFullPath(caseDirectory);
            if (!fullCase.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullCase).StartsWith("case-", StringComparison.Ordinal)
                || (File.GetAttributes(fullCase) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            Directory.Delete(fullCase, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyRunRoot(string workRoot)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(workRoot).Any())
            {
                Directory.Delete(workRoot);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class TerminatingStageObserver(
        AtomicSaveStage targetStage,
        string markerPath) : IAtomicSaveObserver
    {
        public ValueTask OnStageAsync(
            AtomicSaveStage stage,
            string transactionId,
            CancellationToken cancellationToken)
        {
            if (stage != targetStage)
            {
                return ValueTask.CompletedTask;
            }

            _ = transactionId;
            _ = cancellationToken;
            using (var marker = new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                4 * 1024,
                FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(stage.ToString());
                marker.Write(bytes);
                marker.Flush(flushToDisk: true);
            }

            using var currentProcess = Process.GetCurrentProcess();
            try
            {
                currentProcess.Kill(entireProcessTree: false);
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Environment.FailFast("Atomic-save fault boundary termination failed.", exception);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record DestinationEvidence(
        string Outcome,
        int? Length,
        string? Sha256);

    private sealed record JournalEvidence(
        bool Present,
        bool ParseFailed,
        IReadOnlyList<string> Stages);
}
