using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using System.Runtime.InteropServices;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ElliePdf;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(System.IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Size the window according to the design rubric using the monitor DPI scale.
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        uint dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        appWindow.Resize(new SizeInt32((int)(1100 * scale), (int)(760 * scale)));

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
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
