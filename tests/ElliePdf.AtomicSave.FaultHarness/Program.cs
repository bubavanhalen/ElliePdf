using System.Globalization;
using ElliePdf.Infrastructure.Storage;

namespace ElliePdf.AtomicSave.FaultHarness;

internal static class Program
{
    private const int UsageError = 64;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (TryParseChild(args, out var child))
            {
                return await AtomicSaveFaultRunner.RunChildAsync(
                        child.CaseDirectory,
                        child.Stage,
                        child.Seed,
                        child.Iteration,
                        child.PayloadBytes)
                    .ConfigureAwait(false);
            }

            if (!TryParseRun(args, out var options))
            {
                WriteUsage();
                return UsageError;
            }

            var result = await AtomicSaveFaultRunner.RunAsync(options).ConfigureAwait(false);
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{result.Report.Result.ToUpperInvariant()} atomic-save fault harness: " +
                    $"{result.Report.Totals.Passed}/{result.Report.IterationsRequested} passed; " +
                    $"seed {result.Report.Seed}; report {Path.GetFullPath(options.ReportPath)}"));
            if (result.Report.Result != "pass")
            {
                Console.Error.WriteLine(
                    $"Failure artifacts were retained in the isolated temp run root: {result.WorkRoot}");
            }

            return result.Report.Result == "pass" ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Atomic-save fault harness failed: {exception.GetType().Name}.");
            return 1;
        }
    }

    private static bool TryParseRun(string[] args, out AtomicSaveFaultRunOptions options)
    {
        options = null!;
        if (args.Length == 0 || args.Contains("--child", StringComparer.Ordinal))
        {
            return false;
        }

        var values = ParsePairs(args);
        if (values is null
            || !TryGetInt(values, "--iterations", out var iterations)
            || !TryGetInt(values, "--seed", out var seed)
            || !values.TryGetValue("--report", out var reportPath))
        {
            return false;
        }

        var parallelism = TryGetInt(values, "--parallelism", out var parsedParallelism)
            ? parsedParallelism
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        var payloadBytes = TryGetInt(values, "--payload-bytes", out var parsedPayloadBytes)
            ? parsedPayloadBytes
            : 4 * 1024;
        var configuration = values.TryGetValue("--configuration", out var parsedConfiguration)
            ? parsedConfiguration
            : "unspecified";
        var retainSuccessfulArtifacts = values.TryGetValue("--retain-success-artifacts", out var retainValue)
            && bool.TryParse(retainValue, out var retain)
            && retain;
        options = new AtomicSaveFaultRunOptions(
            iterations,
            seed,
            Path.GetFullPath(reportPath),
            parallelism,
            payloadBytes,
            configuration,
            retainSuccessfulArtifacts);
        return true;
    }

    private static bool TryParseChild(string[] args, out ChildOptions child)
    {
        child = null!;
        if (args.Length == 0 || !string.Equals(args[0], "--child", StringComparison.Ordinal))
        {
            return false;
        }

        var values = ParsePairs(args[1..]);
        if (values is null
            || !values.TryGetValue("--case-directory", out var caseDirectory)
            || !values.TryGetValue("--stage", out var stageText)
            || !Enum.TryParse<AtomicSaveStage>(stageText, ignoreCase: false, out var stage)
            || !TryGetInt(values, "--seed", out var seed)
            || !TryGetInt(values, "--iteration", out var iteration)
            || !TryGetInt(values, "--payload-bytes", out var payloadBytes))
        {
            return false;
        }

        child = new ChildOptions(caseDirectory, stage, seed, iteration, payloadBytes);
        return true;
    }

    private static Dictionary<string, string>? ParsePairs(ReadOnlySpan<string> args)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(args[index], args[index + 1]))
            {
                return null;
            }
        }

        return values;
    }

    private static bool TryGetInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        out int value)
    {
        value = default;
        return values.TryGetValue(key, out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            "Usage: ElliePdf.AtomicSave.FaultHarness --iterations <count> --seed <integer> " +
            "--report <absolute-or-relative-json-path> [--parallelism <1-64>] " +
            "[--payload-bytes <512-1048576>] [--configuration <name>] " +
            "[--retain-success-artifacts <true|false>]");
    }

    private sealed record ChildOptions(
        string CaseDirectory,
        AtomicSaveStage Stage,
        int Seed,
        int Iteration,
        int PayloadBytes);
}
