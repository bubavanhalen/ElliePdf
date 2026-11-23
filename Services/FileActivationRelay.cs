using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace ElliePdf.Services;

internal static class FileActivationRelay
{
    private const int MaximumPayloadBytes = 128 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    public static async Task SendAsync(uint instanceId, IReadOnlyList<string> paths)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\0', paths));
        if (payload.Length is 0 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Invalid file activation payload size.");
        }

        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var pipe = new NamedPipeClientStream(".", PipeName(instanceId), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await pipe.ReadExactlyAsync(header.AsMemory(0, 1), timeout.Token).ConfigureAwait(false);
        if (header[0] != 1)
        {
            throw new IOException("The reader did not accept the file activation.");
        }
    }

    public static async Task ListenAsync(
        uint instanceId, Func<string[], bool> queueFiles, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(PipeName(instanceId), PipeDirection.InOut,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    var header = new byte[sizeof(int)];
                    await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                    var size = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (size is <= 0 or > MaximumPayloadBytes)
                    {
                        continue;
                    }

                    var payload = new byte[size];
                    await pipe.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
                    var paths = Encoding.UTF8.GetString(payload).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    header[0] = queueFiles(paths) ? (byte)1 : (byte)0;
                    await pipe.WriteAsync(header.AsMemory(0, 1), timeout.Token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // A launcher may exit before sending the complete request or reading the reply.
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Resume listening after an incomplete request times out.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string PipeName(uint instanceId) => "ElliePdf.FileActivation." + instanceId.ToString(CultureInfo.InvariantCulture);
}
