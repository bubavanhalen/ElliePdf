using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

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

    /// <summary>
    /// Connects the sandboxed worker to its local broker, whose pipe owner is explicitly the
    /// user's SID. Token default owners can instead be an enabled group on elevated hosts.
    /// The launch secret still authenticates every transport envelope after this owner check.
    /// </summary>
    public static async ValueTask<NamedPipeClientStream> ConnectToWorkerBrokerAsync(
        string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Worker broker pipes are supported on Windows only.");
        ValidateName(pipeName);
        if (pipeName.Contains('\\') || pipeName.Contains('/'))
            throw new ArgumentException("A local worker broker pipe name is required.", nameof(pipeName));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            try
            {
                var owner = stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User is null || owner is not SecurityIdentifier ownerSid || !ownerSid.Equals(identity.User))
                    throw new WorkerBrokerOwnershipException();
            }
            catch (Exception exception) when (exception is not WorkerBrokerOwnershipException)
            {
                throw new WorkerBrokerOwnershipException(exception);
            }
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 256 || name.Contains('\0')) throw new ArgumentOutOfRangeException(nameof(name));
    }
}

public sealed class WorkerBrokerOwnershipException : UnauthorizedAccessException
{
    internal WorkerBrokerOwnershipException(Exception? innerException = null)
        : base("The worker broker pipe owner could not be verified as the current user.", innerException) { }
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
