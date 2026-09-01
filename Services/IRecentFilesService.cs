namespace ElliePdf.Services;

public interface IRecentFilesService
{
    IReadOnlyList<string> GetRecentFiles();

    Task<IReadOnlyList<string>> GetRecentFilesAsync(CancellationToken cancellationToken = default);

    Task RecordOpenedAsync(string path, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
