using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElliePdf;
using ElliePdf.Diagnostics;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IUserSettingsService _settingsService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly ISessionStateStore _sessionStateStore;
    private readonly PrivacySafeDiagnostics _diagnostics;
    private readonly IAnnotationStore _annotationStore;

    public SettingsViewModel(
        IUserSettingsService settingsService,
        IRecentFilesService recentFilesService,
        ISessionStateStore sessionStateStore,
        PrivacySafeDiagnostics diagnostics,
        IAnnotationStore annotationStore)
    {
        _settingsService = settingsService;
        _recentFilesService = recentFilesService;
        _sessionStateStore = sessionStateStore;
        _diagnostics = diagnostics;
        _annotationStore = annotationStore;
        LoadFromSettings();
    }

    [ObservableProperty]
    public partial PdfZoomMode DefaultZoomMode { get; set; }

    [ObservableProperty]
    public partial bool ConfirmOverwriteSave { get; set; }

    [ObservableProperty]
    public partial bool ConfirmOrganizeSave { get; set; }

    [ObservableProperty]
    public partial bool AutoSaveCompanion { get; set; }

    [ObservableProperty]
    public partial bool EnableLabs { get; set; }

    [ObservableProperty]
    public partial int RecentFilesMaxCount { get; set; }

    [ObservableProperty]
    public partial bool ReopenLastSession { get; set; }

    [ObservableProperty]
    public partial bool KeepRecentFiles { get; set; }

    [ObservableProperty]
    public partial bool PersistViewState { get; set; }

    [ObservableProperty]
    public partial bool EnableLocalDiagnostics { get; set; }

    [ObservableProperty]
    public partial bool EnableCrashReports { get; set; }

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
        settings.AutoSaveCompanion = AutoSaveCompanion;
        settings.EnableLabs = EnableLabs;
        settings.RecentFilesMaxCount = Math.Clamp(RecentFilesMaxCount, 1, 50);
        settings.ReopenLastSession = ReopenLastSession;
        settings.KeepRecentFiles = KeepRecentFiles;
        settings.PersistViewState = PersistViewState;
        settings.EnableLocalDiagnostics = EnableLocalDiagnostics;
        settings.EnableCrashReports = EnableCrashReports;

        await _settingsService.SaveAsync();
        if (!KeepRecentFiles)
        {
            await _recentFilesService.ClearAsync();
            await _sessionStateStore.ClearAsync(SessionDataKind.Recents);
        }
        if (!ReopenLastSession)
        {
            await _sessionStateStore.ClearAsync(SessionDataKind.ReopenState);
        }
        if (!EnableLocalDiagnostics)
        {
            _diagnostics.DeleteLocalData();
        }

        StatusMessage = AppResources.Get("Settings_StatusSaved");
        StatusSeverity = InfoBarSeverity.Success;
        IsStatusOpen = true;
    }

    public void DismissStatus() => IsStatusOpen = false;

    [RelayCommand]
    private async Task ClearRecentFilesAsync()
    {
        await _recentFilesService.ClearAsync();
        await _sessionStateStore.ClearAsync(SessionDataKind.Recents);
        StatusMessage = AppResources.Get("Settings_StatusRecentsCleared");
        StatusSeverity = InfoBarSeverity.Success;
        IsStatusOpen = true;
    }

    [RelayCommand]
    private async Task ClearViewStateAsync()
    {
        await _sessionStateStore.ClearAsync(SessionDataKind.ViewState);
        StatusMessage = AppResources.Get("Settings_StatusViewStateCleared");
        StatusSeverity = InfoBarSeverity.Success;
        IsStatusOpen = true;
    }

    [RelayCommand]
    private void ClearDiagnostics()
    {
        _diagnostics.DeleteLocalData();
        StatusMessage = AppResources.Get("Settings_StatusDiagnosticsCleared");
        StatusSeverity = InfoBarSeverity.Success;
        IsStatusOpen = true;
    }

    [RelayCommand]
    private void PreviewSupportBundle()
    {
        var preview = _diagnostics.Preview();
        StatusMessage = preview.EventCount switch
        {
            0 => AppResources.Get("Settings_SupportPreviewEmpty"),
            1 => AppResources.Format(
                "Settings_SupportPreviewOne",
                preview.Bytes / 1024d,
                preview.Oldest.ToLocalTime(),
                preview.Newest.ToLocalTime()),
            _ => AppResources.Format(
                "Settings_SupportPreviewMany",
                preview.EventCount,
                preview.Bytes / 1024d,
                preview.Oldest.ToLocalTime(),
                preview.Newest.ToLocalTime())
        };
        StatusSeverity = InfoBarSeverity.Informational;
        IsStatusOpen = true;
    }

    public void ExportSupportBundle(string destinationPath)
    {
        _diagnostics.ExportSupportBundle(destinationPath);
        StatusMessage = AppResources.Get("Settings_StatusSupportExported");
        StatusSeverity = InfoBarSeverity.Success;
        IsStatusOpen = true;
    }

    public async Task ClearRecoveryDataAsync()
    {
        await _annotationStore.ClearAllRecoveryAsync();
        StatusMessage = AppResources.Get("Settings_StatusRecoveryCleared");
        StatusSeverity = InfoBarSeverity.Success;
        IsStatusOpen = true;
    }

    private void LoadFromSettings()
    {
        var settings = _settingsService.Settings;
        DefaultZoomMode = settings.DefaultZoomMode;
        ConfirmOverwriteSave = settings.ConfirmOverwriteSave;
        ConfirmOrganizeSave = settings.ConfirmOrganizeSave;
        AutoSaveCompanion = settings.AutoSaveCompanion;
        EnableLabs = settings.EnableLabs;
        RecentFilesMaxCount = settings.RecentFilesMaxCount;
        ReopenLastSession = settings.ReopenLastSession;
        KeepRecentFiles = settings.KeepRecentFiles;
        PersistViewState = settings.PersistViewState;
        EnableLocalDiagnostics = settings.EnableLocalDiagnostics;
        EnableCrashReports = settings.EnableCrashReports;
    }
}
