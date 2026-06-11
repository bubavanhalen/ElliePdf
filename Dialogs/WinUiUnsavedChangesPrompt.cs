using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf.Dialogs;

public sealed class WinUiUnsavedChangesPrompt : IUnsavedChangesPrompt
{
    public async Task<UnsavedChangesChoice> PromptAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var xamlRoot = GetXamlRoot();
        if (xamlRoot is null)
        {
            return UnsavedChangesChoice.Cancel;
        }

        var dialog = new ContentDialog
        {
            Title = "Unsaved edits",
            Content = $"'{fileName}' has unsaved annotations. What would you like to do?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
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

    private static XamlRoot? GetXamlRoot() =>
        App.Window.Content is FrameworkElement root ? root.XamlRoot : null;
}
