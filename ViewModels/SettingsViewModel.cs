using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElliePdf;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IUserSettingsService _settingsService;

    public SettingsViewModel(IUserSettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings();
    }

    [ObservableProperty]
    public partial PdfZoomMode DefaultZoomMode { get; set; }

    [ObservableProperty]
    public partial bool ConfirmOverwriteSave { get; set; }

    [ObservableProperty]
    public partial bool ConfirmOrganizeSave { get; set; }

    [ObservableProperty]
    public partial int RecentFilesMaxCount { get; set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; private set; } = InfoBarSeverity.Informational;

    public IReadOnlyList<PdfZoomMode> ZoomModeOptions { get; } =
    [
        PdfZoomMode.FitWidth,
        PdfZoomMode.FitPage,
        PdfZoomMode.ActualSize,
        PdfZoomMode.Custom
    ];

    [RelayCommand]
    private async Task SaveAsync()
    {
        var settings = _settingsService.Settings;
        settings.DefaultZoomMode = DefaultZoomMode;
        settings.ConfirmOverwriteSave = ConfirmOverwriteSave;
        settings.ConfirmOrganizeSave = ConfirmOrganizeSave;
        settings.RecentFilesMaxCount = Math.Clamp(RecentFilesMaxCount, 1, 50);

        await _settingsService.SaveAsync();
        StatusMessage = "Settings saved.";
        StatusSeverity = InfoBarSeverity.Success;
        IsStatusOpen = true;
    }

    public void DismissStatus() => IsStatusOpen = false;

    private void LoadFromSettings()
    {
        var settings = _settingsService.Settings;
        DefaultZoomMode = settings.DefaultZoomMode;
        ConfirmOverwriteSave = settings.ConfirmOverwriteSave;
        ConfirmOrganizeSave = settings.ConfirmOrganizeSave;
        RecentFilesMaxCount = settings.RecentFilesMaxCount;
    }
}
