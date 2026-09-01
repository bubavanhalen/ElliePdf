using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf.Dialogs;

public sealed class WinUiUnsavedChangesPrompt : IUnsavedChangesPrompt
{
    private readonly UiHostContext _uiHost;

    public WinUiUnsavedChangesPrompt(UiHostContext uiHost)
    {
        _uiHost = uiHost;
    }

    public async Task<UnsavedChangesChoice> PromptAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var xamlRoot = _uiHost.XamlRoot;
        if (xamlRoot is null)
        {
            return UnsavedChangesChoice.Cancel;
        }

        var dialog = new ContentDialog
        {
            Title = AppResources.Get("Unsaved_Title"),
            Content = AppResources.Format("Unsaved_Message", fileName),
            PrimaryButtonText = AppResources.Get("Common_Save"),
            SecondaryButtonText = AppResources.Get("Common_Discard"),
            CloseButtonText = AppResources.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => UnsavedChangesChoice.Save,
            ContentDialogResult.Secondary => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel
        };
    }
}
