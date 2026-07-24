using System.Text.Json;

namespace ElliePdf.Services;

public sealed class RecentFilesService : IRecentFilesService
{
    private readonly IUserSettingsService _settingsService;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly string _storeFolder;
    private readonly string _storePath;
    private List<string> _entries = [];
    private bool _loaded;

    public RecentFilesService(IUserSettingsService settingsService)
    {
        _settingsService = settingsService;
        _storeFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ElliePdf");
        _storePath = Path.Combine(_storeFolder, "recent.json");
    }

    public IReadOnlyList<string> GetRecentFiles()
    {
        EnsureLoaded();
        return _entries;
    }

    public async Task<IReadOnlyList<string>> GetRecentFilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _entries;
    }

    public async Task RecordOpenedAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);
        _entries.RemoveAll(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase));
        _entries.Insert(0, path);

        var maxEntries = Math.Clamp(_settingsService.Settings.RecentFilesMaxCount, 1, 50);
        if (_entries.Count > maxEntries)
        {
            _entries = _entries.Take(maxEntries).ToList();
        }

        await SaveToDiskAsync(cancellationToken);
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loadGate.Wait();
        try
        {
            if (!_loaded)
            {
                _entries = LoadFromDisk();
                _loaded = true;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (!_loaded)
            {
                _entries = await LoadFromDiskAsync(cancellationToken);
                _loaded = true;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private List<string> LoadFromDisk()
    {
        if (!File.Exists(_storePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_storePath);
            return FilterEntries(JsonSerializer.Deserialize(json, ElliePdfJsonContext.Default.ListString));
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<List<string>> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_storePath);
            var entries = await JsonSerializer.DeserializeAsync(
                stream,
                ElliePdfJsonContext.Default.ListString,
                cancellationToken);
            return FilterEntries(entries);
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveToDiskAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storeFolder);
        await using var stream = File.Create(_storePath);
        await JsonSerializer.SerializeAsync(
            stream,
            _entries,
            ElliePdfJsonContext.Default.ListString,
            cancellationToken);
    }

    private List<string> FilterEntries(List<string>? entries)
    {
        var maxEntries = Math.Clamp(_settingsService.Settings.RecentFilesMaxCount, 1, 50);
        return entries?
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Take(maxEntries)
            .ToList() ?? [];
    }
}
