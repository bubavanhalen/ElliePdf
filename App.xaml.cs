using ElliePdf.Dialogs;
using ElliePdf.Services;
using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace ElliePdf;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPdfService, PdfService>();
        services.AddSingleton<IPdfPasswordPrompt, WinUiPdfPasswordPrompt>();
        services.AddSingleton<IUnsavedChangesPrompt, WinUiUnsavedChangesPrompt>();
        services.AddSingleton<IDocumentOpenService, DocumentOpenService>();
        services.AddSingleton<IRecentFilesService, RecentFilesService>();
        services.AddSingleton<IAnnotationStore, AnnotationStore>();
        services.AddSingleton<IEditSaveService, EditSaveService>();
        services.AddSingleton<ITabCloseService, TabCloseService>();
        services.AddSingleton<IDocumentTabService, DocumentTabService>();
        services.AddSingleton<DocumentCollectionViewModel>();
        services.AddSingleton<ReaderViewModel>();
        Services = services.BuildServiceProvider();

        InitializeComponent();
    }

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
