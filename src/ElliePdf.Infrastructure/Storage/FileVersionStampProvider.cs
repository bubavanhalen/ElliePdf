using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ElliePdf.Domain.Storage;
using Microsoft.Win32.SafeHandles;

namespace ElliePdf.Infrastructure.Storage;

public sealed class FileVersionStampProvider : IFileVersionStampProvider
{
    public async ValueTask<FileVersionStamp?> TryCaptureAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var requestedPath = Path.GetFullPath(path);

        FileStream stream;
        try
        {
            // Deny write and delete sharing while fingerprinting. A stamp must
            // describe one stable file version, never a mixture of concurrent writes.
            stream = new FileStream(
                requestedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = CaptureHandleInformation(stream.SafeFileHandle);
            var canonicalPath = TryGetFinalPath(stream.SafeFileHandle) ?? requestedPath;
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var after = CaptureHandleInformation(stream.SafeFileHandle);

            if (!before.Equals(after))
            {
                throw new IOException("The file changed while its version stamp was being captured.");
            }

            return new FileVersionStamp(
                canonicalPath,
                before.Identity,
                before.Length,
                before.LastWriteUtc,
                Convert.ToHexString(hash));
        }
    }

    private static StableHandleInformation CaptureHandleInformation(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            var nonWindowsLength = RandomAccess.GetLength(handle);
            return new StableHandleInformation(null, nonWindowsLength, DateTimeOffset.MinValue);
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                "Unable to query stable file identity.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        var fileTime = ((long)information.LastWriteTime.dwHighDateTime << 32)
                       | (uint)information.LastWriteTime.dwLowDateTime;
        return new StableHandleInformation(
            $"{information.VolumeSerialNumber:X8}:{fileIndex:X16}",
            length,
            DateTimeOffset.FromFileTime(fileTime));
    }

    private static string? TryGetFinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var required = GetFinalPathNameByHandle(handle, null, 0, 0);
        if (required == 0)
        {
            return null;
        }

        var buffer = new StringBuilder(checked((int)required + 1));
        var written = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (written == 0 || written >= buffer.Capacity)
        {
            return null;
        }

        var value = buffer.ToString();
        const string uncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (value.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + value[uncPrefix.Length..];
        }

        return value.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? value[extendedPrefix.Length..]
            : value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        StringBuilder? filePath,
        uint filePathLength,
        uint flags);

    private readonly record struct StableHandleInformation(
        string? Identity,
        long Length,
        DateTimeOffset LastWriteUtc);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
