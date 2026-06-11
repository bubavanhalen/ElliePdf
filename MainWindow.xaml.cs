using ElliePdf.Services;
using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.Graphics;

namespace ElliePdf;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(System.IntPtr hWnd);

    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        uint dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        appWindow.Resize(new SizeInt32((int)(1100 * scale), (int)(760 * scale)));
        appWindow.Closing += AppWindow_Closing;

        RootFrame.Navigate(typeof(MainPage));
        Closed += OnClosed;
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose)
        {
            return;
        }

        args.Cancel = true;

        var tabCloseService = App.Services.GetRequiredService<ITabCloseService>();
        var canClose = await tabCloseService.TryCloseAllDirtyTabsAsync();
        if (!canClose)
        {
            return;
        }

        _forceClose = true;
        Close();
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            if (App.Services is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (App.Services is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to dispose application services: {ex}");
        }
    }

    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        if (RootFrame.Content is not MainPage mainPage)
        {
            RootFrame.Navigate(typeof(MainPage));
            mainPage = (MainPage)RootFrame.Content;
        }

        await mainPage.OpenFilesAsync(filePaths);
    }
}
