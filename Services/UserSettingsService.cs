using System.Text.Json;

namespace ElliePdf.Services;

public sealed class UserSettingsService : IUserSettingsService
{
    private readonly string _storeFolder;
    private readonly string _storePath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public UserSettingsService()
    {
        _storeFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ElliePdf");
        _storePath = Path.Combine(_storeFolder, "settings.json");
    }

    public UserSettings Settings { get; private set; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_storePath))
        {
            Settings = new UserSettings();
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_storePath);
            Settings = await JsonSerializer.DeserializeAsync(
                stream,
                ElliePdfJsonContext.Default.UserSettings,
                cancellationToken) ?? new UserSettings();
        }
        catch (IOException)
        {
            Settings = new UserSettings();
        }
        catch (JsonException)
        {
            Settings = new UserSettings();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_storeFolder);
        await _saveGate.WaitAsync(cancellationToken);
        var temporaryPath = Path.Combine(_storeFolder, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Settings,
                    ElliePdfJsonContext.Default.UserSettings,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, _storePath, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
