using Microsoft.Win32.SafeHandles;

namespace ElliePdf.Pdfium.Worker;

/// <summary>
/// Sequential stream facade over a brokered handle. Offset-based writes work for handles opened
/// with or without FILE_FLAG_OVERLAPPED and do not depend on a shared cross-process file pointer.
/// </summary>
internal sealed class BrokeredWriteStream : Stream
{
    private SafeFileHandle? _handle;
    private long _position;

    private BrokeredWriteStream(SafeFileHandle handle)
    {
        _handle = handle;
    }

    internal static BrokeredWriteStream CreateTruncated(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            handle.Dispose();
            throw new ArgumentException("A valid brokered write handle is required.", nameof(handle));
        }

        var stream = new BrokeredWriteStream(handle);
        try
        {
            RandomAccess.SetLength(handle, 0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => _handle is { IsClosed: false, IsInvalid: false };
    public override long Length => RandomAccess.GetLength(GetHandle());

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override void Flush()
    {
        _ = GetHandle();
        // The broker owns durable flush and commit ordering after this authority is closed.
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The transaction handle is write-only.");

    public override long Seek(long offset, SeekOrigin origin)
    {
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        ArgumentOutOfRangeException.ThrowIfNegative(next);
        _position = next;
        return next;
    }

    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        RandomAccess.SetLength(GetHandle(), value);
        if (_position > value)
        {
            _position = value;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The requested buffer range is invalid.", nameof(count));
        }

        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        RandomAccess.Write(GetHandle(), buffer, _position);
        _position = checked(_position + buffer.Length);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _handle, null)?.Dispose();
        }

        base.Dispose(disposing);
    }

    private SafeFileHandle GetHandle() => _handle is { IsClosed: false, IsInvalid: false } handle
        ? handle
        : throw new ObjectDisposedException(nameof(BrokeredWriteStream));
}
