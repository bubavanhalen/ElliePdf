using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf.Dialogs;

public sealed class WinUiPdfPasswordPrompt : IPdfPasswordPrompt
{
    public async Task<string?> PromptAsync(PdfPasswordPromptRequest request, CancellationToken cancellationToken = default)
    {
        var xamlRoot = GetXamlRoot();
        if (xamlRoot is null)
        {
            return null;
        }

        var fileName = Path.GetFileName(request.FilePath);
        var message = request.IsRetry
            ? $"The password for '{fileName}' was incorrect. Try again."
            : $"'{fileName}' is protected. Enter the password to open it.";

        var passwordBox = new PasswordBox
        {
            PlaceholderText = "Password",
            Width = 320
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords });
        panel.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            Title = "Password required",
            Content = panel,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? passwordBox.Password : null;
    }

    private static XamlRoot? GetXamlRoot()
    {
        if (App.Window.Content is FrameworkElement root)
        {
            return root.XamlRoot;
        }

        return null;
    }
}
