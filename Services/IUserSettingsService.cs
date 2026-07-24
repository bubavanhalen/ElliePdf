namespace ElliePdf.Services;

public interface IUserSettingsService
{
    UserSettings Settings { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
