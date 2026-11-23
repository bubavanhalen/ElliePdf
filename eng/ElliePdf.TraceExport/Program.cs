using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using ElliePdf.Telemetry;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;

namespace ElliePdf.TraceExport;

internal static class Program
{
    private const string RequiredProvider = "ElliePdf";

    // Payload names are part of ElliePdfEventSource's measurement-only contract.
    // Unknown fields and every string value are omitted, even when a future event
    // accidentally adds them, so exported evidence cannot contain document data.
    private static readonly HashSet<string> AllowedPayloadNames = new(StringComparer.Ordinal)
    {
        "operationId",
        "durationMicroseconds",
        "success",
        "pageCount",
        "pixelWidth",
        "pixelHeight",
        "bytes",
        "reason",
        "resultCount",
        "stage",
        "errorCode",
        "priority",
        "callKind",
        "pageIndex",
        "restartCount",
        "budgetKind",
        "exitCode"
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args is ["--emit-self-test"])
            {
                Thread.Sleep(750);
                ElliePdfEventSource.Log.FirstPagePresented(1, 123_000);
                ElliePdfEventSource.Log.RenderCompleted(2, 456_789, 9_876_543_210);
                ElliePdfEventSource.Log.SaveStage(3, 4, 77_001, true);
                Thread.Sleep(750);
                return 0;
            }

            ExportOptions options = ExportOptions.Parse(args);
            string tracePath = Path.GetFullPath(options.TracePath);
            string outputPath = Path.GetFullPath(options.OutputPath);
            if (!File.Exists(tracePath))
                throw new FileNotFoundException("The input nettrace file was not found.", tracePath);

            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            int exported = 0;
            using var writer = new StreamWriter(outputPath, options.Append, new UTF8Encoding(false));
            using var source = new EventPipeEventSource(tracePath);
            source.Dynamic.All += traceEvent =>
            {
                if (!string.Equals(traceEvent.ProviderName, RequiredProvider, StringComparison.Ordinal))
                    return;

                var payload = CreateSafePayload(traceEvent);
                var record = new ExportRecord(
                    RequiredProvider,
                    traceEvent.EventName ?? string.Empty,
                    unchecked((int)traceEvent.ID),
                    options.Iteration,
                    payload);
                writer.WriteLine(JsonSerializer.Serialize(record));
                exported++;
            };
            source.Process();
            writer.Flush();

            if (exported == 0)
                throw new InvalidDataException("The trace contained no ElliePdf provider events.");

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Exported {exported} privacy-safe ElliePdf events for iteration {options.Iteration}."));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Trace export failed: {exception.Message}");
            return 2;
        }
    }

    private static Dictionary<string, object> CreateSafePayload(TraceEvent traceEvent)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal);
        string[]? payloadNames = traceEvent.PayloadNames;
        if (payloadNames is null)
            return payload;

        for (int index = 0; index < payloadNames.Length; index++)
        {
            string name = payloadNames[index];
            if (!AllowedPayloadNames.Contains(name))
                continue;

            object? value = traceEvent.PayloadValue(index);
            if (TryNormalizeMeasurement(value, out object? measurement))
                payload[name] = measurement;
        }

        return payload;
    }

    private static bool TryNormalizeMeasurement(object? value, out object measurement)
    {
        switch (value)
        {
            case bool boolean:
                measurement = boolean;
                return true;
            case byte number:
                measurement = number;
                return true;
            case sbyte number:
                measurement = number;
                return true;
            case short number:
                measurement = number;
                return true;
            case ushort number:
                measurement = number;
                return true;
            case int number:
                measurement = number;
                return true;
            case uint number:
                measurement = number;
                return true;
            case long number:
                measurement = number;
                return true;
            case ulong number:
                measurement = number;
                return true;
            case float number when float.IsFinite(number):
                measurement = number;
                return true;
            case double number when double.IsFinite(number):
                measurement = number;
                return true;
            default:
                measurement = 0;
                return false;
        }
    }
}

internal sealed record ExportRecord(
    string ProviderName,
    string EventName,
    int EventId,
    int Iteration,
    Dictionary<string, object> Payload);

internal sealed record ExportOptions(string TracePath, string OutputPath, int Iteration, bool Append)
{
    public static ExportOptions Parse(string[] args)
    {
        string? tracePath = null;
        string? outputPath = null;
        int? iteration = null;
        bool append = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--trace":
                    tracePath = ReadValue(args, ref index, argument);
                    break;
                case "--output":
                    outputPath = ReadValue(args, ref index, argument);
                    break;
                case "--iteration":
                    string text = ReadValue(args, ref index, argument);
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
                        throw new ArgumentException("--iteration must be a non-negative integer.");
                    iteration = parsed;
                    break;
                case "--append":
                    append = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{argument}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(tracePath) || string.IsNullOrWhiteSpace(outputPath) || iteration is null)
            throw new ArgumentException("Usage: ElliePdf.TraceExport --trace <file.nettrace> --output <events.jsonl> --iteration <n> [--append]");

        return new ExportOptions(tracePath, outputPath, iteration.Value, append);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }
}
