using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Client;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Pdf.Transport;

namespace ElliePdf.Fuzz.Tests;

internal static class BoundedFuzzHarness
{
    public const int MaxCaseBytes = 2 * 1024 * 1024;

    public static IEnumerable<(string Name, byte[] Frame)> ProtocolCorpus(int count)
    {
        var seed = new byte[] { 0x45, 0x6C, 0x6C, 0x69, 0x65, 0x50, 0x64, 0x66 };
        for (var i = 0; i < count; i++)
        {
            var bodyLength = 1 + (i * 37 % 240);
            var frame = new byte[4 + bodyLength];
            BinaryPrimitives.WriteInt32LittleEndian(frame, i % 5 == 0 ? -1 : bodyLength);
            for (var b = 0; b < bodyLength; b++)
                frame[4 + b] = (byte)(seed[(i + b) % seed.Length] ^ (i * 13 + b * 7));
            if (i % 7 == 0)
                frame[4 + bodyLength - 1] = (byte)'}';
            yield return ($"protocol-{i:D4}", frame);
        }
    }

    public static IEnumerable<(string Name, byte[] Bytes)> PdfCorpusMutations(int count)
    {
        var source = MinimalPdf();
        for (var i = 0; i < count; i++)
        {
            var bytes = source.ToArray();
            if (i == 0)
            {
                yield return ($"pdf-{i:D4}", bytes);
                continue;
            }

            var edits = 1 + i % 8;
            for (var edit = 0; edit < edits; edit++)
            {
                var offset = (i * 17 + edit * 11) % bytes.Length;
                bytes[offset] ^= (byte)(1 + ((i + edit) % 251));
            }

            // Exercise truncated headers, malformed xref/trailer data and trailing garbage;
            // every mutation is deterministic and remains small enough for the worker contract.
            if (i % 3 == 0)
                bytes = bytes[..Math.Max(1, bytes.Length - 1)];
            if (i % 5 == 0)
                bytes[Math.Min(bytes.Length - 1, 8 + i % 32)] = 0;
            if (i % 7 == 0)
                bytes = bytes.Concat(Encoding.ASCII.GetBytes("\n%%FUZZ\n" + i)).ToArray();
            yield return ($"pdf-{i:D4}", bytes);
        }
    }

    public static byte[] MinimalPdf()
    {
        // Build the offsets instead of embedding a fixture. This keeps the fuzz test
        // self-contained while giving PDFium one valid seed for the recovery assertion.
        var bytes = new List<byte>(512);
        var offsets = new List<int> { 0 };
        Append("%PDF-1.4\n");
        AddObject("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        AddObject("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        AddObject("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources << >> /Contents 4 0 R >>\nendobj\n");
        AddObject("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");
        var xrefOffset = bytes.Count;
        Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            Append($"{offset:D10} 00000 n \n");
        Append($"trailer\n<< /Root 1 0 R /Size 5 >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return bytes.ToArray();

        void AddObject(string value)
        {
            offsets.Add(bytes.Count);
            Append(value);
        }

        void Append(string value) => bytes.AddRange(Encoding.ASCII.GetBytes(value));
    }

    public static string PrivacySafeOutcome(string name, ReadOnlySpan<byte> input, string outcome)
        => $"{name} sha256={Convert.ToHexString(SHA256.HashData(input))} outcome={outcome}";

    public static async Task<string> ExerciseProtocolAsync(byte[] frame, TimeSpan deadline)
    {
        using var stream = new MemoryStream(frame, writable: false);
        var codec = new LengthPrefixedFrameCodec(new TransportCodecOptions { MaxFrameBytes = 4096, MaxStringLength = 4096 });
        using var cts = new CancellationTokenSource(deadline);
        try
        {
            _ = await codec.ReadAsync(stream, cts.Token);
            return "accepted";
        }
        catch (OperationCanceledException) { return "deadline"; }
        catch (Exception ex) when (ex is TransportProtocolException or EndOfStreamException or JsonException)
        {
            return ex.GetType().Name;
        }
    }

    public static WorkerFuzzMode ReadMode()
        => Enum.TryParse<WorkerFuzzMode>(
            Environment.GetEnvironmentVariable("ELLIEPDF_FUZZ_MODE"),
            ignoreCase: true,
            out var mode)
            ? mode
            : WorkerFuzzMode.Smoke;

    public static string FindWorkerExecutable()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The process isolation fuzz gate requires Windows.");

        var root = RepositoryRoot();
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var platform = architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
            _ => throw new PlatformNotSupportedException($"Unsupported worker architecture: {architecture}.")
        };
        var configuration = Environment.GetEnvironmentVariable("ELLIEPDF_FUZZ_CONFIGURATION") ?? "Release";
        var roots = new[]
        {
            Path.Combine(root, "src", "ElliePdf.Pdfium.Worker", "bin", platform, configuration),
            Path.Combine(root, "src", "ElliePdf.Pdfium.Worker", "bin", configuration)
        };
        var matches = roots
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "ElliePdf.Pdfium.Worker.exe", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment =>
                string.Equals(segment, "native", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "publish", StringComparison.OrdinalIgnoreCase)))
            .Where(path =>
                File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "coreclr.dll"))
                && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "pdfium.dll"))
                && WorkerProtocolMatchesCurrentBuild(Path.GetDirectoryName(path)!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matches.Length > 0
            ? matches[0]
            : throw new FileNotFoundException(
                "A self-contained PDF worker matching the current protocol build was not found.",
                roots[0]);
    }

    private static bool WorkerProtocolMatchesCurrentBuild(string candidateDirectory)
        => FilesMatch(
                Path.Combine(candidateDirectory, "ElliePdf.Pdf.Transport.dll"),
                typeof(WorkerOperation).Assembly.Location)
            && FilesMatch(
                Path.Combine(candidateDirectory, "ElliePdf.Pdf.Contracts.dll"),
                typeof(PdfContractVersion).Assembly.Location);

    private static bool FilesMatch(string candidate, string current)
    {
        if (!File.Exists(candidate) || !File.Exists(current))
        {
            return false;
        }

        using var candidateStream = File.OpenRead(candidate);
        using var currentStream = File.OpenRead(current);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(candidateStream),
            SHA256.HashData(currentStream));
    }

    public static async Task<WorkerFuzzReport> ExerciseRealWorkerAsync(
        WorkerFuzzMode mode,
        CancellationToken cancellationToken = default)
    {
        var limits = WorkerFuzzLimits.For(mode);
        var workerPath = FindWorkerExecutable();
        var baselinePids = WorkerPids(workerPath);
        var outcomes = new List<WorkerFuzzCaseOutcome>(limits.CaseCount);
        var crashes = 0;
        var hangs = 0;
        var unexpected = 0;
        var restarted = 0;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"elliepdf-fuzz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        await using var client = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = workerPath,
            StartupTimeout = TimeSpan.FromSeconds(10),
            DefaultOperationTimeout = limits.CaseDeadline,
            HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
            RequireAppContainerSandbox = true,
            RequireRestrictedTokenSandbox = true
        });

        try
        {
            foreach (var (name, bytes) in PdfCorpusMutations(limits.CaseCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes.Length is < 1 or > MaxCaseBytes)
                    throw new InvalidDataException("The deterministic fuzz corpus exceeded its size bound.");

                var beforePid = WorkerPid(client);
                var path = Path.Combine(temporaryDirectory, $"{name}-{Guid.NewGuid():N}.pdf");
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                var outcome = await ExerciseCaseAsync(client, path, name, bytes, limits.CaseDeadline).ConfigureAwait(false);
                outcomes.Add(outcome);
                if (outcome.Outcome == "worker-crash") crashes++;
                if (outcome.Outcome == "deadline") hangs++;
                if (outcome.Outcome == "unexpected") unexpected++;
                var afterPid = WorkerPid(client);
                if (beforePid is not null && afterPid is not null && beforePid.Value != afterPid.Value)
                    restarted++;
                File.Delete(path);

                // A hang is a fail-closed condition. The process has already been killed by
                // ExerciseCaseAsync, and continuing would hide an unsafe worker state.
                if (outcome.Outcome == "deadline")
                    break;
            }
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch (IOException) { }
        }

        await client.DisposeAsync().ConfigureAwait(false);
        var leaked = await WaitForWorkerExitAsync(workerPath, baselinePids, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return new WorkerFuzzReport(mode, outcomes, crashes, hangs, unexpected, restarted, leaked);
    }

    public static async Task ExerciseWorkerRestartAsync(CancellationToken cancellationToken = default)
    {
        var workerPath = FindWorkerExecutable();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"elliepdf-fuzz-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var path = Path.Combine(temporaryDirectory, "seed.pdf");
        await File.WriteAllBytesAsync(path, MinimalPdf(), cancellationToken).ConfigureAwait(false);
        try
        {
            await using var client = new PdfWorkerClient(new PdfWorkerClientOptions
            {
                WorkerExecutablePath = workerPath,
                StartupTimeout = TimeSpan.FromSeconds(10),
                DefaultOperationTimeout = TimeSpan.FromSeconds(2),
                HeartbeatInterval = TimeSpan.FromMilliseconds(250),
                HeartbeatTimeout = TimeSpan.FromSeconds(2),
                RequireAppContainerSandbox = true,
                RequireRestrictedTokenSandbox = true
            });
            await using var session = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(path)), cancellationToken);
            var before = WorkerProcess(client) ?? throw new InvalidOperationException("Worker process was not observable after startup.");
            var hostPid = Environment.ProcessId;
            try
            {
                // The client owns a Job Object for descendants; killing the worker itself keeps
                // this deterministic and avoids Process.KillTree probing a short-lived handle.
                if (!before.HasExited)
                    before.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException)
            {
            }
            await WaitForWorkerExitAsync(client, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await AssertNeverCompletesAfterCrashAsync(session, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.Equal(hostPid, Environment.ProcessId);

            await using var reopened = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(path)), cancellationToken);
            var metadata = await reopened.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (metadata.PageCount != 1)
                throw new InvalidDataException("The restarted worker did not reopen the deterministic seed PDF.");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<WorkerFuzzCaseOutcome> ExerciseCaseAsync(
        PdfWorkerClient client,
        string path,
        string name,
        byte[] bytes,
        TimeSpan deadline)
    {
        var operation = OpenAndReadMetadataAsync(client, path, deadline);
        var completed = await Task.WhenAny(operation, Task.Delay(deadline)).ConfigureAwait(false);
        if (completed != operation)
        {
            KillWorkerProcess(client);
            await ObserveBoundedAsync(operation).ConfigureAwait(false);
            return WorkerFuzzCaseOutcome.Create(name, bytes, "deadline");
        }

        try
        {
            await operation.ConfigureAwait(false);
            return WorkerFuzzCaseOutcome.Create(name, bytes, "accepted");
        }
        catch (PdfWorkerRemoteException)
        {
            return WorkerFuzzCaseOutcome.Create(name, bytes, "rejected");
        }
        catch (PdfWorkerUnavailableException)
        {
            return WorkerFuzzCaseOutcome.Create(name, bytes, "worker-crash");
        }
        catch (OperationCanceledException)
        {
            return WorkerFuzzCaseOutcome.Create(name, bytes, "cancelled");
        }
        catch
        {
            return WorkerFuzzCaseOutcome.Create(name, bytes, "unexpected");
        }
    }

    private static async Task OpenAndReadMetadataAsync(PdfWorkerClient client, string path, TimeSpan deadline)
    {
        using var timeout = new CancellationTokenSource(deadline);
        await using var session = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(path)), timeout.Token);
        _ = await session.GetMetadataAsync(timeout.Token).ConfigureAwait(false);
    }

    private static async Task ObserveBoundedAsync(Task operation)
    {
        try { await operation.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); }
        catch { /* outcome is deliberately reduced to a safe class below */ }
    }

    private static void KillWorkerProcess(PdfWorkerClient client)
    {
        var process = WorkerProcess(client);
        if (process is null || process.HasExited)
            return;
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static Process? WorkerProcess(PdfWorkerClient client)
        => (Process?)typeof(PdfWorkerClient)
            .GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client);

    private static int? WorkerPid(PdfWorkerClient client)
    {
        var process = WorkerProcess(client);
        return process is { HasExited: false } ? process.Id : null;
    }

    private static HashSet<int> WorkerPids(string workerPath)
        => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(workerPath))
            .Where(process => !process.HasExited)
            .Select(process => process.Id)
            .ToHashSet();

    private static async Task<int> WaitForWorkerExitAsync(string workerPath, HashSet<int> baseline, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var leaked = WorkerPids(workerPath).Except(baseline).ToArray();
            if (leaked.Length == 0)
                return 0;
            await Task.Delay(25).ConfigureAwait(false);
        }

        var remaining = WorkerPids(workerPath).Except(baseline).ToArray();
        foreach (var pid in remaining)
        {
            try { using var process = Process.GetProcessById(pid); process.Kill(entireProcessTree: true); } catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return remaining.Length;
    }

    private static async Task WaitForWorkerExitAsync(PdfWorkerClient client, TimeSpan timeout)
    {
        var process = WorkerProcess(client);
        if (process is null)
            return;
        try { await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { KillWorkerProcess(client); }
        catch (InvalidOperationException) { }
    }

    private static async Task AssertNeverCompletesAfterCrashAsync(IPdfEngineSession session, TimeSpan timeout)
    {
        var operation = session.GetMetadataAsync(CancellationToken.None).AsTask();
        var completed = await Task.WhenAny(operation, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != operation)
            throw new TimeoutException("The client did not fail a session after worker termination.");
        try
        {
            await operation.ConfigureAwait(false);
            throw new InvalidOperationException("A session unexpectedly succeeded after its worker terminated.");
        }
        catch (PdfWorkerUnavailableException)
        {
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

internal enum WorkerFuzzMode
{
    Smoke,
    Nightly,
    Release
}

internal sealed record WorkerFuzzLimits(int CaseCount, TimeSpan CaseDeadline)
{
    public static WorkerFuzzLimits For(WorkerFuzzMode mode) => mode switch
    {
        WorkerFuzzMode.Smoke => new(16, TimeSpan.FromSeconds(2)),
        WorkerFuzzMode.Nightly => new(256, TimeSpan.FromSeconds(2)),
        WorkerFuzzMode.Release => new(1024, TimeSpan.FromSeconds(2)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

internal sealed record WorkerFuzzCaseOutcome(string Name, string Sha256, string Outcome)
{
    public static WorkerFuzzCaseOutcome Create(string name, ReadOnlySpan<byte> bytes, string outcome)
        => new(name, Convert.ToHexString(SHA256.HashData(bytes)), outcome);

    public string PrivacySafeLine() => $"{Name} sha256={Sha256} outcome={Outcome}";
}

internal sealed record WorkerFuzzReport(
    WorkerFuzzMode Mode,
    IReadOnlyList<WorkerFuzzCaseOutcome> Cases,
    int WorkerCrashCount,
    int DeadlineCount,
    int UnexpectedOutcomeCount,
    int WorkerRestartCount,
    int LeakedWorkerProcessCount)
{
    public IEnumerable<string> PrivacySafeLines()
    {
        yield return $"mode={Mode} cases={Cases.Count} workerCrashes={WorkerCrashCount} deadlines={DeadlineCount} unexpected={UnexpectedOutcomeCount} restarts={WorkerRestartCount} leakedWorkers={LeakedWorkerProcessCount}";
        foreach (var item in Cases)
            yield return item.PrivacySafeLine();
    }
}
