using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;

namespace ElliePdf.Pdfium.Worker;

/// <summary>
/// Exercises ambient authorities from inside the real worker binary. This is intentionally a
/// launch-only release-gate probe: it never runs in authenticated service mode and reports only a
/// bit mask, never file contents or credentials.
/// </summary>
internal sealed record SandboxBoundaryProbe(
    string ReadPath,
    string WritePath,
    string MappingName,
    int LoopbackPort)
{
    private const int ReadPathAccessible = 1;
    private const int WritePathAccessible = 2;
    private const int AmbientMappingAccessible = 4;
    private const int LoopbackNetworkAccessible = 8;

    public static bool TryParse(string[] args, out SandboxBoundaryProbe probe)
    {
        probe = null!;
        if (args.Length != 9 || !string.Equals(args[0], "--sandbox-probe", StringComparison.Ordinal))
        {
            return false;
        }

        string? readPath = null;
        string? writePath = null;
        string? mappingName = null;
        var loopbackPort = 0;
        for (var index = 1; index < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--read-path":
                    readPath = args[index + 1];
                    break;
                case "--write-path":
                    writePath = args[index + 1];
                    break;
                case "--mapping":
                    mappingName = args[index + 1];
                    break;
                case "--loopback-port":
                    _ = int.TryParse(
                        args[index + 1],
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out loopbackPort);
                    break;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(readPath)
            || string.IsNullOrWhiteSpace(writePath)
            || string.IsNullOrWhiteSpace(mappingName)
            || loopbackPort is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            return false;
        }

        probe = new SandboxBoundaryProbe(readPath, writePath, mappingName, loopbackPort);
        return true;
    }

    public async Task<int> RunAsync()
    {
        var result = 0;
        try
        {
            await using var input = new FileStream(
                ReadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            _ = input.ReadByte();
            result |= ReadPathAccessible;
        }
        catch (Exception exception) when (IsAuthorityDenied(exception))
        {
        }

        try
        {
            await File.WriteAllTextAsync(WritePath, "sandbox boundary violation").ConfigureAwait(false);
            result |= WritePathAccessible;
        }
        catch (Exception exception) when (IsAuthorityDenied(exception))
        {
        }

        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.Read);
            result |= AmbientMappingAccessible;
        }
        catch (Exception exception) when (IsAuthorityDenied(exception))
        {
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(IPAddress.Loopback, LoopbackPort, timeout.Token).ConfigureAwait(false);
            result |= LoopbackNetworkAccessible;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
        }

        return result;
    }

    private static bool IsAuthorityDenied(Exception exception)
        => exception is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException;
}
