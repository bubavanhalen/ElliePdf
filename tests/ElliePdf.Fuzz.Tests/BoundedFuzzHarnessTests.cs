using System.Text.Json;

namespace ElliePdf.Fuzz.Tests;

public sealed class BoundedFuzzHarnessTests
{
    [Fact]
    public async Task ProtocolSmokeCorpus_is_bounded_and_completes()
    {
        var outcomes = new List<string>();
        foreach (var (name, frame) in BoundedFuzzHarness.ProtocolCorpus(128))
        {
            Assert.InRange(frame.Length, 4, BoundedFuzzHarness.MaxCaseBytes);
            var outcome = await BoundedFuzzHarness.ExerciseProtocolAsync(frame, TimeSpan.FromMilliseconds(100));
            outcomes.Add(BoundedFuzzHarness.PrivacySafeOutcome(name, frame, outcome));
        }

        Assert.Equal(128, outcomes.Count);
        Assert.All(outcomes, line => Assert.Matches("^protocol-\\d{4} sha256=[0-9A-F]{64} outcome=", line));
    }

    [Fact]
    public void PdfMutationSmokeCorpus_is_deterministic_and_bounded()
    {
        var first = BoundedFuzzHarness.PdfCorpusMutations(256)
            .Select(x => BoundedFuzzHarness.PrivacySafeOutcome(x.Name, x.Bytes, x.Bytes.Length.ToString()))
            .ToArray();
        var second = BoundedFuzzHarness.PdfCorpusMutations(256)
            .Select(x => BoundedFuzzHarness.PrivacySafeOutcome(x.Name, x.Bytes, x.Bytes.Length.ToString()))
            .ToArray();

        Assert.Equal(first, second);
        Assert.All(BoundedFuzzHarness.PdfCorpusMutations(256), item => Assert.InRange(item.Bytes.Length, 1, BoundedFuzzHarness.MaxCaseBytes));
        Assert.DoesNotContain(first, line => line.Contains("%PDF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProtocolSmoke_never_emits_input_or_exception_messages()
    {
        foreach (var (name, frame) in BoundedFuzzHarness.ProtocolCorpus(32))
        {
            var outcome = await BoundedFuzzHarness.ExerciseProtocolAsync(frame, TimeSpan.FromMilliseconds(100));
            var report = BoundedFuzzHarness.PrivacySafeOutcome(name, frame, outcome);
            Assert.DoesNotContain("Malformed JSON", report);
            Assert.DoesNotContain("Ellie", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToHexString(frame), report, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact(Timeout = 900_000)]
    public async Task Real_worker_pdf_fuzz_is_bounded_isolated_and_privacy_safe()
    {
        var report = await BoundedFuzzHarness.ExerciseRealWorkerAsync(BoundedFuzzHarness.ReadMode());
        var lines = report.PrivacySafeLines().ToArray();
        foreach (var line in lines)
            Console.WriteLine(line);
        var reportPath = Environment.GetEnvironmentVariable("ELLIEPDF_FUZZ_REPORT_PATH");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllLines(reportPath, lines);
        }

        Assert.NotEmpty(report.Cases);
        Assert.Contains(report.Cases, item => item.Outcome == "accepted");
        Assert.Equal(0, report.DeadlineCount);
        Assert.Equal(0, report.UnexpectedOutcomeCount);
        Assert.Equal(0, report.WorkerCrashCount);
        Assert.Equal(0, report.LeakedWorkerProcessCount);
        Assert.All(report.Cases, item =>
        {
            Assert.Matches("^pdf-\\d{4}$", item.Name);
            Assert.Matches("^[0-9A-F]{64}$", item.Sha256);
            Assert.DoesNotContain("%PDF", item.PrivacySafeLine(), StringComparison.Ordinal);
        });
    }

    [Fact(Timeout = 120_000)]
    public async Task Real_worker_fuzz_client_survives_worker_termination_and_restart()
    {
        // This intentionally terminates only the worker child. The test-host PID assertion and
        // successful reopen prove that the UI-side client boundary remains usable.
        await BoundedFuzzHarness.ExerciseWorkerRestartAsync();
    }
}
