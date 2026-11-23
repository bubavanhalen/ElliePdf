using System.Security.Cryptography;
using System.Text;

namespace ElliePdf.Infrastructure.Storage;

public interface IAtomicDestinationPolicy
{
    void EnsureSupported(string destinationPath);
}

public sealed class LocalAtomicDestinationPolicy : IAtomicDestinationPolicy
{
    private static readonly HashSet<string> SupportedWindowsFileSystems =
        new(StringComparer.OrdinalIgnoreCase) { "NTFS", "ReFS" };

    public void EnsureSupported(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var canonicalPath = Path.GetFullPath(destinationPath);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (canonicalPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new AtomicCommitNotSupportedException(
                "Network and UNC destinations are Save As only because atomic replacement cannot be proven.",
                new IOException("UNC destination rejected by policy."));
        }

        var root = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new AtomicCommitNotSupportedException(
                "The destination volume could not be identified.",
                new IOException("Destination has no volume root."));
        }

        var drive = new DriveInfo(root);
        if (!drive.IsReady
            || drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Unknown or DriveType.NoRootDirectory
            || !SupportedWindowsFileSystems.Contains(drive.DriveFormat))
        {
            throw new AtomicCommitNotSupportedException(
                "The destination does not provide ElliePdf's required local atomic-replacement guarantees. Use Save As on NTFS or ReFS.",
                new IOException($"Unsupported destination volume '{drive.DriveType}/{drive.DriveFormat}'."));
        }
    }
}

public interface ICrossProcessDestinationLockProvider
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        string identity,
        CancellationToken cancellationToken = default);
}

public sealed class CrossProcessDestinationLockProvider : ICrossProcessDestinationLockProvider
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly string _lockDirectory;

    public CrossProcessDestinationLockProvider(string? lockDirectory = null)
    {
        _lockDirectory = lockDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ElliePdf",
            "TransactionLocks");
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        Directory.CreateDirectory(_lockDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity.ToUpperInvariant())));
        var lockPath = Path.Combine(_lockDirectory, $"{hash}.lck");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                return new FileStreamDestinationLock(stream);
            }
            catch (IOException)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class FileStreamDestinationLock(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
