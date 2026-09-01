using System.IO.Pipes;

namespace ElliePdf.Pdf.Transport;

public static class NamedPipeTransport
{
    public static void EnsureCurrentUserOnly()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Current-user-only named pipes are supported on Windows only.");
    }

    public static NamedPipeServerStream CreateServer(string pipeName, int maxInstances = 1)
    {
        EnsureCurrentUserOnly();
        ValidateName(pipeName);
        if (maxInstances is < 1 or > 254) throw new ArgumentOutOfRangeException(nameof(maxInstances));
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public static NamedPipeClientStream CreateClient(string pipeName)
    {
        EnsureCurrentUserOnly();
        ValidateName(pipeName);
        return new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 256 || name.Contains('\0')) throw new ArgumentOutOfRangeException(nameof(name));
    }
}

public sealed class NamedPipeServer : IAsyncDisposable
{
    private readonly string _name;
    private readonly int _maxInstances;
    private NamedPipeServerStream? _current;
    public NamedPipeServer(string pipeName, int maxInstances = 1) { _name = pipeName; _maxInstances = maxInstances; }

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        _current = NamedPipeTransport.CreateServer(_name, _maxInstances);
        try
        {
            await _current.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _current;
        }
        catch { await _current.DisposeAsync().ConfigureAwait(false); _current = null; throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_current is not null) await _current.DisposeAsync().ConfigureAwait(false);
        _current = null;
    }
}

public sealed class NamedPipeClient : IAsyncDisposable
{
    private readonly string _name;
    private NamedPipeClientStream? _stream;
    public NamedPipeClient(string pipeName) { _name = pipeName; }
    public Stream Stream => _stream ?? throw new InvalidOperationException("The client is not connected.");

    public async ValueTask<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _stream = NamedPipeTransport.CreateClient(_name);
        await _stream.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
        return _stream;
    }
    public async ValueTask DisposeAsync()
    {
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
        _stream = null;
    }
}
