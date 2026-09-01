using ElliePdf.Pdf.Transport;
using System.Security.Cryptography;

namespace ElliePdf.Pdfium.Worker;

internal static class Program
{
    private const int UsageError = 64;
    private const int AuthenticationError = 65;
    private const int WorkerFailure = 70;

    public static async Task<int> Main(string[] args)
    {
        if (SandboxBoundaryProbe.TryParse(args, out var probe))
        {
            return await probe.RunAsync().ConfigureAwait(false);
        }

        if (args.Length == 1 && string.Equals(args[0], "--self-test", StringComparison.Ordinal))
        {
            await using var registry = new WorkerDocumentRegistry(AppContext.BaseDirectory);
            await registry.Ready.ConfigureAwait(false);
            return 0;
        }

        if (!TryParseServeArguments(args, out var pipeName, out var sessionId))
        {
            Console.Error.WriteLine("ElliePdf.Pdfium.Worker must be launched by the authenticated ElliePdf broker.");
            return UsageError;
        }

        var secretBytes = new byte[LaunchSecret.ByteLength];
        try
        {
            if (!await ReadExactlyAsync(Console.OpenStandardInput(), secretBytes).ConfigureAwait(false))
            {
                Console.Error.WriteLine("Worker authentication bootstrap failed.");
                return AuthenticationError;
            }

            var secret = new LaunchSecret(secretBytes);
            await using var client = new NamedPipeClient(pipeName);
            var stream = await client.ConnectAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await using var server = new PdfWorkerServer(sessionId, secret, AppContext.BaseDirectory);
            await server.RunAsync(stream).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Worker terminated: {exception.GetType().Name}.");
            return WorkerFailure;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static bool TryParseServeArguments(string[] args, out string pipeName, out Guid sessionId)
    {
        pipeName = string.Empty;
        sessionId = Guid.Empty;
        if (args.Length != 5 || !string.Equals(args[0], "--serve", StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = 1; index < args.Length; index += 2)
        {
            if (string.Equals(args[index], "--pipe", StringComparison.Ordinal))
            {
                pipeName = args[index + 1];
            }
            else if (string.Equals(args[index], "--session", StringComparison.Ordinal))
            {
                _ = Guid.TryParseExact(args[index + 1], "D", out sessionId);
            }
            else
            {
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(pipeName)
            && pipeName.Length <= 256
            && sessionId != Guid.Empty;
    }

    private static async Task<bool> ReadExactlyAsync(Stream input, Memory<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await input.ReadAsync(destination[total..]).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
