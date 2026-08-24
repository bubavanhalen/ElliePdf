using CommunityToolkit.Mvvm.ComponentModel;
using ElliePdf;
using ElliePdf.Helpers;
using ElliePdf.Services;

namespace ElliePdf.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly PdfZoomMode[] ZoomModes =
    [
        PdfZoomMode.FitWidth,
        PdfZoomMode.FitPage,
        PdfZoomMode.ActualSize,
        PdfZoomMode.Custom
    ];

    private readonly IUserSettingsService _settingsService;
    private bool _isLoading;

    public SettingsViewModel(IUserSettingsService settingsService)
    {
        _settingsService = settingsService;

        var version = typeof(SettingsViewModel).Assembly.GetName().Version;
        AppVersion = version is null ? "1.0" : $"{version.Major}.{version.Minor}.{version.Build}";

        LoadFromSettings();
    }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial int DefaultZoomModeIndex { get; set; }

    [ObservableProperty]
    public partial bool ConfirmOverwriteSave { get; set; }

    [ObservableProperty]
    public partial bool ConfirmOrganizeSave { get; set; }

    [ObservableProperty]
    public partial bool AutoSaveCompanion { get; set; }

    [ObservableProperty]
    public partial int RecentFilesMaxCount { get; set; }

    public List<string> ThemeOptions { get; } = ["Use system setting", "Light", "Dark"];

    public List<string> ZoomModeOptions { get; } = ["Fit width", "Fit page", "Actual size", "Custom"];

    public string AppVersion { get; }

    partial void OnThemeIndexChanged(int value)
    {
        if (!_isLoading)
        {
            ThemeHelper.Apply(ThemeIndexToName(value));
        }

        ApplyAndSave();
    }

    partial void OnDefaultZoomModeIndexChanged(int value) => ApplyAndSave();

    partial void OnConfirmOverwriteSaveChanged(bool value) => ApplyAndSave();

    partial void OnConfirmOrganizeSaveChanged(bool value) => ApplyAndSave();

    partial void OnAutoSaveCompanionChanged(bool value) => ApplyAndSave();

    partial void OnRecentFilesMaxCountChanged(int value) => ApplyAndSave();

    private void ApplyAndSave()
    {
        if (_isLoading)
        {
            return;
        }

        var settings = _settingsService.Settings;
        settings.AppTheme = ThemeIndexToName(ThemeIndex);
        settings.DefaultZoomMode = ZoomModes[Math.Clamp(DefaultZoomModeIndex, 0, ZoomModes.Length - 1)];
        settings.ConfirmOverwriteSave = ConfirmOverwriteSave;
        settings.ConfirmOrganizeSave = ConfirmOrganizeSave;
        settings.AutoSaveCompanion = AutoSaveCompanion;
        settings.RecentFilesMaxCount = Math.Clamp(RecentFilesMaxCount, 1, 50);

        _ = _settingsService.SaveAsync();
    }

    private static string ThemeIndexToName(int index) => index switch
    {
        1 => "Light",
        2 => "Dark",
        _ => "System"
    };

    private static int ThemeNameToIndex(string? name) => name switch
    {
        "Light" => 1,
        "Dark" => 2,
        _ => 0
    };

    private void LoadFromSettings()
    {
        _isLoading = true;
        try
        {
            var settings = _settingsService.Settings;
            ThemeIndex = ThemeNameToIndex(settings.AppTheme);
            DefaultZoomModeIndex = Math.Max(0, Array.IndexOf(ZoomModes, settings.DefaultZoomMode));
            ConfirmOverwriteSave = settings.ConfirmOverwriteSave;
            ConfirmOrganizeSave = settings.ConfirmOrganizeSave;
            AutoSaveCompanion = settings.AutoSaveCompanion;
            RecentFilesMaxCount = settings.RecentFilesMaxCount;
        }
        finally
        {
            _isLoading = false;
        }
    }
}
