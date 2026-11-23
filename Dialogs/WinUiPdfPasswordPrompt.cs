using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf.Dialogs;

public sealed class WinUiPdfPasswordPrompt : IPdfPasswordPrompt
{
    private readonly UiHostContext _uiHost;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public WinUiPdfPasswordPrompt(UiHostContext uiHost)
    {
        _uiHost = uiHost;
    }

    public async Task<string?> PromptAsync(PdfPasswordPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _dialogGate.WaitAsync(cancellationToken);
        try
        {
            return await _uiHost.RunOnUiThreadAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var xamlRoot = _uiHost.XamlRoot
                    ?? throw new InvalidOperationException("The UI host does not have a XamlRoot.");

                var fileName = Path.GetFileName(request.FilePath);
                var message = request.IsRetry
                    ? AppResources.Format("PasswordPrompt_Incorrect", fileName)
                    : AppResources.Format("PasswordPrompt_Protected", fileName);

                var passwordBox = new PasswordBox
                {
                    PlaceholderText = AppResources.Get("PasswordPrompt_Placeholder"),
                    Width = 320
                };

                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords });
                panel.Children.Add(passwordBox);

                var dialog = new ContentDialog
                {
                    Title = AppResources.Get("PasswordPrompt_Title"),
                    Content = panel,
                    PrimaryButtonText = AppResources.Get("Common_Open"),
                    CloseButtonText = AppResources.Get("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot
                };

                using var cancellationRegistration = cancellationToken.Register(() =>
                    dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
                var result = await dialog.ShowAsync();
                cancellationToken.ThrowIfCancellationRequested();
                return result == ContentDialogResult.Primary ? passwordBox.Password : null;
            }, cancellationToken);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

}
