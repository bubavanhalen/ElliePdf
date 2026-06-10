using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ElliePdf;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// The application-wide host providing DI services.
    /// </summary>
    public static Microsoft.Extensions.Hosting.IHost AppHost { get; private set; } = null!;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        // Build a minimal generic host so services can be resolved (ViewModels, app services).
        AppHost = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<ViewModels.MainPageViewModel>();
                services.AddSingleton<ViewModels.EditPageViewModel>();
                services.AddSingleton<ViewModels.DocumentCollectionViewModel>();
                services.AddSingleton<Services.IPdfService, Services.PdfService>();
            })
            .Build();

        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
        _ = ProcessInitialActivationAsync();
    }

    private static async Task ProcessInitialActivationAsync()
    {
        if (Window is not MainWindow mainWindow)
        {
            return;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind != ExtendedActivationKind.File)
        {
            return;
        }

        if (activation.Data is not FileActivatedEventArgs fileArgs)
        {
            return;
        }

        var filePaths = fileArgs.Files
            .OfType<StorageFile>()
            .Where(file => string.Equals(file.FileType, ".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (filePaths.Length == 0)
        {
            return;
        }

        await mainWindow.OpenFilesAsync(filePaths);
    }
}
