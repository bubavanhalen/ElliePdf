using Microsoft.UI.Xaml;

namespace ElliePdf.Services;

/// <summary>
/// Instance-scoped access to the active WinUI host. The composition root attaches
/// the window once; UI consumers receive this context explicitly through DI.
/// </summary>
public sealed class UiHostContext
{
    private MainWindow? _window;

    public MainWindow Window =>
        _window ?? throw new InvalidOperationException("The UI host has not been attached.");

    public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public Microsoft.UI.WindowId WindowId =>
        Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowHandle);

    public XamlRoot? XamlRoot =>
        _window?.Content is FrameworkElement root ? root.XamlRoot : null;

    internal void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null && !ReferenceEquals(_window, window))
        {
            throw new InvalidOperationException("A different UI host is already attached.");
        }

        _window = window;
    }

    internal void Detach(MainWindow window)
    {
        if (ReferenceEquals(_window, window))
        {
            _window = null;
        }
    }

    public void SetTitleBar(UIElement titleBar) => Window.SetTitleBar(titleBar);

    public Task OpenFilesAsync(IReadOnlyList<string> filePaths) =>
        Window.OpenFilesAsync(filePaths);
}
