using ElliePdf.Services;
using ElliePdf.ViewModels;
using ElliePdf.Diagnostics;
using ElliePdf.Pages;
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
    private bool _closeEvaluationInProgress;
    private readonly AppWindow _appWindow;
    private readonly TaskCompletionSource _rootLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MainPage _mainPage;
    private readonly ITabCloseService _tabCloseService;
    private readonly IDocumentTabService _tabService;
    private readonly ReaderViewModel _readerViewModel;
    private readonly IUserSettingsService _settingsService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly ISessionStateStore _sessionStateStore;
    private readonly PrivacySafeDiagnostics _diagnostics;
    private readonly UiHostContext _uiHost;

    public MainWindow(
        MainPage mainPage,
        ITabCloseService tabCloseService,
        IDocumentTabService tabService,
        ReaderViewModel readerViewModel,
        IUserSettingsService settingsService,
        IRecentFilesService recentFilesService,
        ISessionStateStore sessionStateStore,
        PrivacySafeDiagnostics diagnostics,
        UiHostContext uiHost)
    {
        _mainPage = mainPage;
        _tabCloseService = tabCloseService;
        _tabService = tabService;
        _readerViewModel = readerViewModel;
        _settingsService = settingsService;
        _recentFilesService = recentFilesService;
        _sessionStateStore = sessionStateStore;
        _diagnostics = diagnostics;
        _uiHost = uiHost;
        InitializeComponent();
        _uiHost.Attach(this);

        var uiSettings = new Windows.UI.ViewManagement.UISettings();
        if (!uiSettings.AdvancedEffectsEnabled)
        {
            SystemBackdrop = null;
        }

        ExtendsContentIntoTitleBar = true;

        AppWindow.SetIcon("Assets/AppIcon.ico");

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        uint dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        _appWindow.ResizeClient(new SizeInt32((int)(1100 * scale), (int)(760 * scale)));
        _appWindow.Closing += AppWindow_Closing;
        _appWindow.Changed += AppWindow_Changed;

        if (!uiSettings.AnimationsEnabled)
        {
            RootFrame.ContentTransitions?.Clear();
        }

        RootFrame.Content = _mainPage;
        RootFrame.Loaded += (_, _) => _rootLoaded.TrySetResult();
        Closed += OnClosed;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var minimumWidth = (int)Math.Ceiling(ReaderShellPolicy.MinimumWidth * scale);
        var minimumHeight = (int)Math.Ceiling(ReaderShellPolicy.MinimumHeight * scale);
        if (sender.ClientSize.Width < minimumWidth || sender.ClientSize.Height < minimumHeight)
        {
            sender.ResizeClient(new SizeInt32(
                Math.Max(sender.ClientSize.Width, minimumWidth),
                Math.Max(sender.ClientSize.Height, minimumHeight)));
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose)
        {
            return;
        }

        args.Cancel = true;

        if (_closeEvaluationInProgress)
        {
            return;
        }

        _closeEvaluationInProgress = true;

        try
        {
            var canClose = await _tabCloseService.TryCloseAllDirtyTabsAsync();
            if (!canClose)
            {
                return;
            }

            try
            {
                await SaveSessionStateAsync();
            }
            catch (Exception ex)
            {
                RecordSessionFailure("session-save-failed", ex);
            }

            _forceClose = true;
            Close();
        }
        finally
        {
            _closeEvaluationInProgress = false;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _appWindow.Changed -= AppWindow_Changed;
        _uiHost.Detach(this);
    }

    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        await _mainPage.OpenFilesAsync(filePaths);
    }

    internal Task WaitForUiReadyAsync() => _rootLoaded.Task;

    internal ReaderPage BenchmarkReaderPage => _mainPage.BenchmarkReaderPage;

    internal async Task CloseForBenchmarkAsync()
    {
        _forceClose = true;
        Close();
        await Task.CompletedTask;
    }

    public void BringToForeground()
    {
        if (!_appWindow.IsVisible)
        {
            _appWindow.Show();
        }

        Activate();
        _appWindow.MoveInZOrderAtTop();
    }

    public void RestoreWindowState(SessionWindowState? state)
    {
        if (state is null)
        {
            return;
        }

        var display = DisplayArea.GetFromPoint(
            new PointInt32(state.X, state.Y),
            DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var minimumWidth = Math.Min(
            workArea.Width,
            (int)Math.Ceiling(ReaderShellPolicy.MinimumWidth * scale));
        var minimumHeight = Math.Min(
            workArea.Height,
            (int)Math.Ceiling(ReaderShellPolicy.MinimumHeight * scale));
        var width = Math.Clamp((int)Math.Round(state.Width), minimumWidth, workArea.Width);
        var height = Math.Clamp((int)Math.Round(state.Height), minimumHeight, workArea.Height);
        var x = Math.Clamp(state.X, workArea.X, workArea.X + workArea.Width - width);
        var y = Math.Clamp(state.Y, workArea.Y, workArea.Y + workArea.Height - height);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

        if (state.IsMaximized && _appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    public async Task RestoreSessionAsync(
        SessionStateDocument state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Tabs.Count == 0)
        {
            return;
        }

        var restorableTabs = state.Tabs
            .Where(static tab => File.Exists(tab.Path))
            .DistinctBy(static tab => tab.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (restorableTabs.Length == 0)
        {
            return;
        }

        var desiredActivePath = restorableTabs.Any(tab =>
                string.Equals(tab.Path, state.ActiveTabPath, StringComparison.OrdinalIgnoreCase))
            ? state.ActiveTabPath
            : restorableTabs[^1].Path;
        var tabService = _tabService;

        foreach (var tabState in restorableTabs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await tabService.RestoreTabAsync(
                    tabState,
                    string.Equals(tabState.Path, desiredActivePath, StringComparison.OrdinalIgnoreCase),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RecordSessionFailure("session-document-restore-cancelled", null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                RecordSessionFailure("session-document-restore-failed", ex);
            }
        }

        var activeState = restorableTabs.FirstOrDefault(tab =>
            string.Equals(tab.Path, tabService.ActiveTab?.FilePath, StringComparison.OrdinalIgnoreCase));
        if (activeState is null)
        {
            return;
        }

        var viewModel = _readerViewModel;
        if (activeState.ViewMode == "single")
        {
            viewModel.UseSinglePageViewCommand.Execute(null);
        }
        else
        {
            await viewModel.UseContinuousViewCommand.ExecuteAsync(null);
        }

        viewModel.ClosePanels();
        if (activeState.SidebarOpen)
        {
            switch (activeState.SidebarMode)
            {
                case "outline":
                    viewModel.ToggleOutlinePanelCommand.Execute(null);
                    break;
                case "search":
                    viewModel.ToggleSearchPanelCommand.Execute(null);
                    break;
                default:
                    viewModel.ToggleThumbnailPanelCommand.Execute(null);
                    break;
            }
        }

        await viewModel.RefreshFromSessionAsync();
    }

    private async Task SaveSessionStateAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.Settings;
        var tabService = _tabService;
        var viewModel = _readerViewModel;
        var recentFiles = await _recentFilesService.GetRecentFilesAsync(cancellationToken);
        var viewMode = viewModel.ViewMode == PdfReaderViewMode.SinglePage ? "single" : "continuous";
        var sidebarMode = viewModel.IsOutlinePanelOpen
            ? "outline"
            : viewModel.IsSearchPanelOpen
                ? "search"
                : "thumbnails";

        var tabs = tabService.Tabs.Select(tab => new SessionTabState
        {
            Path = tab.FilePath,
            PageIndex = Math.Max(0, tab.CurrentPageIndex),
            Zoom = Math.Clamp(tab.ZoomScale, 0.1, 64),
            ZoomMode = FormatZoomMode(tab.ZoomMode),
            ViewMode = viewMode,
            SidebarOpen = viewModel.IsSidebarOpen,
            SidebarMode = sidebarMode,
            IsLockedPlaceholder = tab.IsLockedPlaceholder || tab.OpenSession?.IsEncrypted == true
        }).ToArray();

        var position = _appWindow.Position;
        var clientSize = _appWindow.ClientSize;
        var state = new SessionStateDocument
        {
            Tabs = tabs,
            RecentFiles = recentFiles,
            ActiveTabPath = tabService.ActiveTab?.FilePath,
            Window = new SessionWindowState
            {
                Width = clientSize.Width,
                Height = clientSize.Height,
                X = position.X,
                Y = position.Y,
                IsMaximized = _appWindow.Presenter is OverlappedPresenter
                {
                    State: OverlappedPresenterState.Maximized
                }
            }
        };
        var policy = new SessionPrivacyPolicy(
            settings.ReopenLastSession,
            settings.KeepRecentFiles,
            settings.PersistViewState,
            settings.EnableLocalDiagnostics);
        await _sessionStateStore.SaveAsync(state, policy, cancellationToken);
    }

    private static string FormatZoomMode(PdfZoomMode mode) => mode switch
    {
        PdfZoomMode.Custom => "custom",
        PdfZoomMode.FitPage => "fitPage",
        PdfZoomMode.ActualSize => "actualSize",
        _ => "fitWidth"
    };

    private void RecordSessionFailure(string eventName, Exception? exception)
    {
        if (!_settingsService.Settings.EnableLocalDiagnostics)
        {
            return;
        }

        _diagnostics.Write(new DiagnosticEvent(
            eventName,
            exception?.GetType().Name ?? "OperationCanceled",
            new Dictionary<string, object?>()));
    }
}
