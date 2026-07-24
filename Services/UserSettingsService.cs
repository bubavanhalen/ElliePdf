using System.Text.Json;

namespace ElliePdf.Services;

public sealed class UserSettingsService : IUserSettingsService
{
    private readonly string _storeFolder;
    private readonly string _storePath;

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
        await using var stream = File.Create(_storePath);
        await JsonSerializer.SerializeAsync(
            stream,
            Settings,
            ElliePdfJsonContext.Default.UserSettings,
            cancellationToken);
    }
}
