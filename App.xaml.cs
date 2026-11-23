using System.Runtime.InteropServices;
using ElliePdf.Application;
using ElliePdf.Benchmarking;
using ElliePdf.Diagnostics;
using ElliePdf.Dialogs;
using ElliePdf.Infrastructure.Storage;
using ElliePdf.Navigation;
using ElliePdf.Pdf.Client;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Printing;
using ElliePdf.Rendering;
using ElliePdf.Services;
using ElliePdf.Telemetry;
using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace ElliePdf;

public partial class App : Microsoft.UI.Xaml.Application
{
    private const string MainInstanceKey = "ElliePdf.Main";
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private readonly TaskCompletionSource _initialActivationQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _launchOperationId = TelemetryOperation.NextId();
    private readonly long _launchStarted = TelemetryOperation.StartTimestamp();
    private readonly BenchmarkDriverRequest? _benchmarkDriver = ParseBenchmarkDriver();
    private readonly ServiceProvider _services;
    private AppInstance? _mainInstance;
    private CancellationTokenSource? _activationRelayCancellation;
    private MainWindow? _window;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private Task? _disposeServicesTask;
    private SessionStateDocument _startupSession = new();
    private bool _sessionRestored;

    public App()
    {
        ElliePdfEventSource.Log.AppLaunchStart(_launchOperationId);
        var services = new ServiceCollection();
        services.AddSingleton(static _ => new PrivacySafeDiagnostics(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ElliePdf",
            "Diagnostics")));
        services.AddSingleton<BackgroundTaskSupervisor>();
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddSingleton<ISessionStateStore>(static _ => new AtomicSessionStateStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ElliePdf",
            "session.json")));
        services.AddSingleton<IFileVersionStampProvider, FileVersionStampProvider>();
        services.AddSingleton<IAtomicSaveObserver, TelemetryAtomicSaveObserver>();
        services.AddSingleton<IAtomicDocumentStore, AtomicDocumentStore>();
        services.AddSingleton<EngineJobScheduler>();
        services.AddSingleton(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = Path.Combine(
                AppContext.BaseDirectory,
                "PdfWorker",
                "ElliePdf.Pdfium.Worker.exe")
        });
        services.AddSingleton<PdfWorkerClient>();
        services.AddSingleton<IPdfEngineClient>(static provider =>
            provider.GetRequiredService<PdfWorkerClient>());
        services.AddSingleton<IPdfService, PdfService>();
        services.AddSingleton<PrintPipeline>();
        services.AddSingleton<IPdfPasswordPrompt, WinUiPdfPasswordPrompt>();
        services.AddSingleton<IUnsavedChangesPrompt, WinUiUnsavedChangesPrompt>();
        services.AddSingleton<IDocumentOpenService, DocumentOpenService>();
        services.AddSingleton<WorkspacePdfEngineSessionFactory>();
        services.AddSingleton<ElliePdf.Application.IPdfEngineSessionFactory>(static provider =>
            provider.GetRequiredService<WorkspacePdfEngineSessionFactory>());
        services.AddSingleton<DocumentWorkspace>();
        services.AddSingleton<IRecentFilesService, RecentFilesService>();
        services.AddSingleton<IAnnotationStore, AnnotationStore>();
        services.AddSingleton<IEditSaveService, EditSaveService>();
        services.AddSingleton<ITabCloseService, TabCloseService>();
        services.AddSingleton<IDocumentTabService, DocumentTabService>();
        services.AddSingleton<AppNavigation>();
        services.AddSingleton<UiHostContext>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DocumentCollectionViewModel>();
        services.AddSingleton<ReaderViewModel>();
        services.AddSingleton<Pages.ReaderPage>();
        services.AddSingleton<Pages.SettingsPage>();
        services.AddSingleton<Pages.OrganizePage>();
        services.AddSingleton<MainPage>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();
        var backgroundTasks = _services.GetRequiredService<BackgroundTaskSupervisor>();
        var settingsService = _services.GetRequiredService<IUserSettingsService>();
        var diagnostics = _services.GetRequiredService<PrivacySafeDiagnostics>();
        backgroundTasks.Faulted += fault =>
        {
            if (!settingsService.Settings.EnableLocalDiagnostics)
            {
                return;
            }

            diagnostics.Write(new DiagnosticEvent(
                "background-task-fault",
                fault.Exception.GetType().Name,
                new Dictionary<string, object?>
                {
                    ["operation"] = fault.Name,
                    ["error"] = fault.Exception.Message
                }));
        };

        InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var activationOperationId = TelemetryOperation.NextId();
        var activationStarted = TelemetryOperation.StartTimestamp();
        ElliePdfEventSource.Log.ActivationStart(activationOperationId);
        ElliePdfEventSource.Log.ActivationReceived(activationOperationId);
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var launchArguments = activation.Data is ILaunchActivatedEventArgs launch ? launch.Arguments : args.Arguments;
        ReaderDesignPreview.Configure(string.IsNullOrWhiteSpace(launchArguments)
            ? string.Join(' ', Environment.GetCommandLineArgs().Skip(1))
            : launchArguments);
        if (_benchmarkDriver is not null)
        {
            var succeeded = false;
            try
            {
                succeeded = await LaunchBenchmarkDriverAsync(_benchmarkDriver);
            }
            finally
            {
                ElliePdfEventSource.Log.ActivationStop(
                    activationOperationId,
                    TelemetryOperation.ElapsedMicroseconds(activationStarted),
                    succeeded);
            }
            return;
        }

        var files = GetActivationFiles(activation, Environment.GetCommandLineArgs().Skip(1));
        var registeredInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
        if (!registeredInstance.IsCurrent)
        {
            var redirected = false;
            try
            {
                if (files.Length > 0)
                {
                    // Packaged classic launches can receive file paths only on the process command line.
                    await FileActivationRelay.SendAsync(registeredInstance.ProcessId, files);
                }
                else
                {
                    await registeredInstance.RedirectActivationToAsync(activation);
                }
                redirected = true;
            }
            finally
            {
                ElliePdfEventSource.Log.ActivationStop(
                    activationOperationId,
                    TelemetryOperation.ElapsedMicroseconds(activationStarted),
                    redirected);
                await DisposeServicesAsync();
                // This instance never creates a window or acquires a document session.
                Environment.Exit(redirected ? 0 : 1);
            }
            return;
        }

        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _mainInstance = registeredInstance;
        _mainInstance.Activated += OnMainInstanceActivated;
        _activationRelayCancellation = new CancellationTokenSource();
        _ = _services.GetRequiredService<BackgroundTaskSupervisor>().Track(
            FileActivationRelay.ListenAsync(registeredInstance.ProcessId,
                paths => QueueRedirectedActivation(() => FilterPdfPaths(paths)), _activationRelayCancellation.Token),
            "file-activation-relay");

        var settingsService = _services.GetRequiredService<IUserSettingsService>();
        await settingsService.InitializeAsync();
        if (settingsService.Settings.ReopenLastSession || settingsService.Settings.PersistViewState)
        {
            _startupSession = await _services.GetRequiredService<ISessionStateStore>().LoadAsync();
        }

        _window = _services.GetRequiredService<MainWindow>();
        _window.Closed += OnWindowClosed;
        if (settingsService.Settings.PersistViewState)
        {
            _window.RestoreWindowState(_startupSession.Window);
        }
        _window.Activate();
        ElliePdfEventSource.Log.ShellInteractive(
            _launchOperationId,
            TelemetryOperation.ElapsedMicroseconds(_launchStarted));
        _ = _services.GetRequiredService<BackgroundTaskSupervisor>()
            .Track(ProcessActivationAsync(files, activationOperationId, activationStarted), "initial-activation");
        _initialActivationQueued.TrySetResult();
    }

    private void OnMainInstanceActivated(object? sender, AppActivationArguments activation) =>
        QueueRedirectedActivation(() => GetActivationFiles(activation));

    private bool QueueRedirectedActivation(Func<string[]> getFiles)
    {
        var operationId = TelemetryOperation.NextId();
        var started = TelemetryOperation.StartTimestamp();
        ElliePdfEventSource.Log.ActivationStart(operationId);
        ElliePdfEventSource.Log.ActivationReceived(operationId);
        return _dispatcherQueue?.TryEnqueue(() =>
        {
            _ = _services.GetRequiredService<BackgroundTaskSupervisor>()
                .Track(ProcessRedirectedActivationAsync(getFiles(), operationId, started), "redirected-activation");
        }) == true;
    }

    private async Task ProcessRedirectedActivationAsync(string[] files, int operationId, long started)
    {
        await _initialActivationQueued.Task;
        await ProcessActivationAsync(files, operationId, started);
    }

    private async Task ProcessActivationAsync(
        IReadOnlyList<string> filePaths,
        int operationId,
        long started)
    {
        var succeeded = false;
        await _activationGate.WaitAsync();
        try
        {
            if (_window is not { } mainWindow)
            {
                return;
            }

            if (!_sessionRestored)
            {
                _sessionRestored = true;
                if (_services.GetRequiredService<IUserSettingsService>().Settings.ReopenLastSession)
                {
                    await mainWindow.RestoreSessionAsync(_startupSession);
                }
            }

            if (filePaths.Count > 0)
            {
                await mainWindow.OpenFilesAsync(filePaths);
            }

            mainWindow.BringToForeground();
            succeeded = true;
        }
        finally
        {
            _activationGate.Release();
            ElliePdfEventSource.Log.ActivationStop(
                operationId,
                TelemetryOperation.ElapsedMicroseconds(started),
                succeeded);
        }
    }

    private static string[] GetActivationFiles(
        AppActivationArguments activation,
        IEnumerable<string>? processArguments = null)
    {
        IEnumerable<string> paths = activation.Data switch
        {
            IFileActivatedEventArgs file => file.Files.OfType<StorageFile>().Select(static item => item.Path),
            ILaunchActivatedEventArgs launch => SplitLaunchArguments(launch.Arguments),
            _ => []
        };
        if (activation.Kind == ExtendedActivationKind.Launch && processArguments is not null)
        {
            paths = paths.Concat(processArguments);
        }

        return FilterPdfPaths(paths);
    }

    private static string[] FilterPdfPaths(IEnumerable<string> paths) =>
        paths.Where(static path => Path.IsPathFullyQualified(path)
                && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] SplitLaunchArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        // Supply a program name so CommandLineToArgvW applies regular quoting rules to every argument.
        var argv = CommandLineToArgvW("ElliePdf " + arguments, out var count);
        if (argv == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var result = new string[count - 1];
            for (var index = 1; index < count; index++)
            {
                result[index - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? "";
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    private Task DisposeServicesAsync() =>
        _disposeServicesTask ??= _services.DisposeAsync().AsTask();

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
        }

        if (_mainInstance is not null)
        {
            _mainInstance.Activated -= OnMainInstanceActivated;
        }
        _activationRelayCancellation?.Cancel();

        try
        {
            await DisposeServicesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to dispose application services: {ex}");
        }
        finally
        {
            _activationRelayCancellation?.Dispose();
        }
    }

    private static BenchmarkDriverRequest? ParseBenchmarkDriver() =>
        BenchmarkDriverRequest.TryParse(Environment.GetCommandLineArgs(), out var request) ? request : null;

    private async Task<bool> LaunchBenchmarkDriverAsync(BenchmarkDriverRequest request)
    {
        _window = _services.GetRequiredService<MainWindow>();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _window.Closed += OnWindowClosed;
        _window.Activate();
        ElliePdfEventSource.Log.ShellInteractive(
            _launchOperationId,
            TelemetryOperation.ElapsedMicroseconds(_launchStarted));

        try
        {
            await _window.WaitForUiReadyAsync();
            var fixture = Environment.GetEnvironmentVariable(BenchmarkDriverRequest.FixtureEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture))
            {
                return false;
            }

            var openStarted = TelemetryOperation.StartTimestamp();
            await _window.OpenFilesAsync(new[] { fixture });
            BenchmarkDriver.WriteMetric(
                "open.completed",
                "ms",
                TelemetryOperation.ElapsedMicroseconds(openStarted) / 1000d);
            await BenchmarkDriver.RunAsync(
                request,
                _services.GetRequiredService<ReaderViewModel>(),
                _services.GetRequiredService<IPdfService>(),
                _services.GetRequiredService<PdfWorkerClient>(),
                _window.BenchmarkReaderPage,
                CancellationToken.None);
            BenchmarkDriver.WriteReady(request);

            // The collector snapshots immediately after readiness. Keeping the UI alive
            // briefly gives it a stable process tree, while direct invocations still
            // shut down their worker and window without user interaction.
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            return true;
        }
        catch (Exception)
        {
            // A benchmark target must never leak a path, exception message, or document
            // text through stdout. Failure is signalled by withholding readiness.
        }
        finally
        {
            await _window.CloseForBenchmarkAsync();
            await DisposeServicesAsync();
            Exit();
        }

        return false;
    }
}
