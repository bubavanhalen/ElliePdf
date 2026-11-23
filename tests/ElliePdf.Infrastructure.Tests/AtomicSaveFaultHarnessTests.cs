using System.Diagnostics;
using System.Text.Json;
using ElliePdf.AtomicSave.FaultHarness;
using ElliePdf.Infrastructure.Storage;
using Xunit;

namespace ElliePdf.Infrastructure.Tests;

public sealed class AtomicSaveFaultHarnessTests
{
    [Fact(Timeout = 30_000)]
    public async Task Child_mode_refuses_an_arbitrary_temp_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var arbitraryDirectory = Path.Combine(
            Path.GetTempPath(),
            "elliepdf-tests",
            $"atomic-fault-denial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(arbitraryDirectory);
        var destinationPath = Path.Combine(arbitraryDirectory, "document.pdf");
        var original = Enumerable.Repeat((byte)0x5a, 1024).ToArray();
        await File.WriteAllBytesAsync(destinationPath, original);
        try
        {
            var harnessAssembly = typeof(AtomicSaveFaultRunner).Assembly.Location;
            var appHost = Path.ChangeExtension(harnessAssembly, ".exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = File.Exists(appHost) ? appHost : "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!File.Exists(appHost))
            {
                startInfo.ArgumentList.Add(harnessAssembly);
            }

            foreach (var argument in new[]
                     {
                         "--child",
                         "--case-directory", arbitraryDirectory,
                         "--stage", AtomicSaveStage.DestinationLockAcquired.ToString(),
                         "--seed", "1729",
                         "--iteration", "0",
                         "--payload-bytes", "1024"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var child = Process.Start(startInfo);
            Assert.NotNull(child);
            await child.WaitForExitAsync();
            Assert.NotEqual(0, child.ExitCode);
            Assert.Equal(original, await File.ReadAllBytesAsync(destinationPath));
            Assert.False(File.Exists(Path.Combine(arbitraryDirectory, "boundary.reached")));
        }
        finally
        {
            try
            {
                Directory.Delete(arbitraryDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Child_process_fault_smoke_covers_every_transaction_stage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "elliepdf-tests",
            $"atomic-fault-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, "report.json");
        try
        {
            var stageCount = AtomicSaveFaultRunner.StageNames.Count;
            var result = await AtomicSaveFaultRunner.RunAsync(new AtomicSaveFaultRunOptions(
                Iterations: checked(stageCount * 2),
                Seed: 1729,
                ReportPath: reportPath,
                Parallelism: 2,
                PayloadBytes: 1024,
                Configuration: "PR-smoke"));

            Assert.Equal("pass", result.Report.Result);
            Assert.Matches("^[0-9a-f]{32}$", result.Report.RunId);
            Assert.Equal(stageCount * 2, result.Report.IterationsCompleted);
            Assert.Equal(0, result.Report.Totals.Failed);
            Assert.Equal(0, result.Report.Invariants.InvalidDestinationCount);
            Assert.Equal(0, result.Report.Invariants.MissingDestinationCount);
            Assert.Equal(0, result.Report.Invariants.BoundaryNotReachedCount);
            Assert.Equal(0, result.Report.Invariants.ChildTerminationFailureCount);
            Assert.Equal(0, result.Report.Invariants.JournalParseFailureCount);
            Assert.Equal(0, result.Report.Invariants.OutcomeUnknownCount);
            Assert.All(result.Report.StageCoverage, stage =>
            {
                Assert.Equal(2, stage.Iterations);
                Assert.Equal(2, stage.Passed);
            });
            Assert.All(result.Report.Cases, evidence =>
            {
                Assert.True(evidence.BoundaryReached);
                Assert.False(evidence.TimedOut);
                Assert.NotNull(evidence.ChildExitCode);
                Assert.NotEqual(0, evidence.ChildExitCode);
                Assert.NotEqual(AtomicSaveFaultRunner.ChildUnexpectedCompletionExitCode, evidence.ChildExitCode);
                Assert.Contains(evidence.Outcome, new[] { "old", "new" });
                Assert.Equal(
                    evidence.Outcome == "old" ? evidence.OldSha256 : evidence.NewSha256,
                    evidence.DestinationSha256);
                Assert.DoesNotContain("OutcomeUnknown", evidence.JournalStages);
            });

            var reportJson = await File.ReadAllTextAsync(reportPath);
            Assert.DoesNotContain(
                JsonSerializer.Serialize(Path.GetTempPath()),
                reportJson,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                JsonSerializer.Serialize(Environment.UserName),
                reportJson,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                JsonSerializer.Serialize(Environment.MachineName),
                reportJson,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("destinationPath", reportJson, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(result.WorkRoot));
        }
        finally
        {
            try
            {
                Directory.Delete(reportDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
