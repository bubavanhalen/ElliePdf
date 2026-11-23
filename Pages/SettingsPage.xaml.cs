using ElliePdf.ViewModels;
using ElliePdf.Application;
using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ElliePdf.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly BackgroundTaskSupervisor _backgroundTasks;
    private readonly UiHostContext _uiHost;

    public SettingsPage(
        SettingsViewModel viewModel,
        BackgroundTaskSupervisor backgroundTasks,
        UiHostContext uiHost)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _backgroundTasks = backgroundTasks;
        _uiHost = uiHost;
        DataContext = ViewModel;
    }

    public SettingsViewModel ViewModel { get; }

    private void ClearRecovery_Click(object sender, RoutedEventArgs e)
    {
        _backgroundTasks.Track(
            ConfirmAndClearRecoveryAsync(), "clear-recovery-data");
    }

    private void ExportSupport_Click(object sender, RoutedEventArgs e)
    {
        _backgroundTasks.Track(
            ExportSupportBundleAsync(), "export-support-bundle");
    }

    private async Task ExportSupportBundleAsync()
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = AppResources.Get("Settings_SupportSuggestedFileName")
        };
        picker.FileTypeChoices.Add(
            AppResources.Get("Settings_SupportFileType"),
            [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _uiHost.WindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            ViewModel.ExportSupportBundle(file.Path);
        }
    }

    private async Task ConfirmAndClearRecoveryAsync()
    {
        var dialog = new ContentDialog
        {
            Title = AppResources.Get("Settings_ClearRecoveryConfirmTitle"),
            Content = AppResources.Get("Settings_ClearRecoveryConfirmMessage"),
            PrimaryButtonText = AppResources.Get("Settings_ClearRecoveryConfirmDelete"),
            CloseButtonText = AppResources.Get("Settings_ClearRecoveryConfirmCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearRecoveryDataAsync();
        }
    }
}
