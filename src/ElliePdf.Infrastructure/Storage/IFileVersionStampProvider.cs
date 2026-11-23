using ElliePdf.Domain.Storage;

namespace ElliePdf.Infrastructure.Storage;

public interface IFileVersionStampProvider
{
    ValueTask<FileVersionStamp?> TryCaptureAsync(
        string path,
        CancellationToken cancellationToken = default);
}
