using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElliePdf.Application;
using ElliePdf.Helpers;
using ElliePdf.Infrastructure.Storage;
using ElliePdf.Models;
using ElliePdf.Navigation;
using ElliePdf.Rendering;
using ElliePdf.Services;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Client;
using ElliePdf.Semantics;
using ElliePdf.Telemetry;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using ContractSearchResult = ElliePdf.Pdf.Contracts.SearchResult;
using FormValue = ElliePdf.Pdf.Contracts.FormValue;
using PdfExternalLinkPolicy = ElliePdf.Pdf.Contracts.PdfExternalLinkPolicy;
using ExternalLinkDecision = ElliePdf.Pdf.Contracts.ExternalLinkDecision;
using PdfLinkKind = ElliePdf.Pdf.Contracts.PdfLinkKind;
using PdfPermissions = ElliePdf.Pdf.Contracts.PdfPermissions;

namespace ElliePdf.ViewModels;

public sealed partial class ReaderViewModel : ObservableObject, IDisposable
{
    private readonly IDocumentTabService _tabService;
    private readonly IPdfService _pdfService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly ITabCloseService _tabCloseService;
    private readonly IAnnotationStore _annotationStore;
    private readonly IEditSaveService _editSaveService;
    private readonly DocumentCollectionViewModel _documentCollectionViewModel;
    private readonly IUserSettingsService _settingsService;
    private readonly BackgroundTaskSupervisor _backgroundTasks;
    private readonly AppNavigation _navigation;
    private readonly UiHostContext _uiHost;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _continuousRenderCts;
    private CancellationTokenSource? _singleViewportRenderCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _semanticCts;
    private double _viewportWidth = 800;
    private double _viewportHeight = 900;
    private float _pageWidthPoints = 612f;
    private float _pageHeightPoints = 792f;
    private readonly List<TextMatch> _searchMatches = [];
    private int _activeSearchMatchIndex = -1;
    private PdfDocumentSession? _continuousDocument;
    private double _continuousScale;
    private bool _isTrackingContinuousScroll;
    private PageExtentIndex? _continuousPageExtents;
    private readonly RenderRasterCache<ImageSource> _gpuTileCache =
        new(RenderCacheBudgets.Default.GpuTileBudgetBytes);
    private readonly MetadataCache<ThumbnailCacheKey, BitmapImage> _thumbnailCache =
        new(RenderCacheBudgets.Default.ThumbnailBudgetBytes);
    private readonly Dictionary<int, RenderKey[]> _activeContinuousTileKeys = [];
    private RenderGeneration _singleRenderGeneration = RenderGeneration.Initial;
    private RenderGeneration _continuousRenderGeneration = RenderGeneration.Initial;
    private PageViewport _singleViewport = new(0, 0, 800, 900);
    private double _rasterizationScale = 1;
    private RenderMode _renderMode;
    private PdfDocumentSession? _semanticDocument;
    private readonly Dictionary<Guid, SemanticReaderController> _semanticControllers = [];
    private readonly Dictionary<(Guid DocumentId, int PageIndex), Task<SemanticPageSnapshot>> _semanticLoads = [];
    private readonly MetadataCache<SemanticCacheKey, SemanticPageSnapshot> _semanticPageCache =
        new(RenderCacheBudgets.Default.MetadataBudgetBytes);
    private readonly HashSet<SemanticCacheKey> _semanticCacheKeys = [];
    private readonly HashSet<Guid> _firstPageRequests = [];
    private readonly HashSet<Guid> _firstPagePresentations = [];
    private CancellationTokenSource? _thumbnailCts;
    private Guid? _thumbnailDocumentId;

    public ObservableCollection<DocumentTabItemViewModel> TabItems { get; } = [];

    public ObservableCollection<PageThumbnailViewModel> PageThumbnails { get; } = [];

    public BulkObservableCollection<RenderedPageViewModel> ContinuousPages { get; } = [];

    public ObservableCollection<RenderedTileViewModel> PageTiles { get; } = [];

    public ObservableCollection<RecentFileItemViewModel> RecentFiles { get; } = [];

    public ObservableCollection<OutlineItemViewModel> OutlineItems { get; } = [];

    public ObservableCollection<SearchResultItemViewModel> SearchResults { get; } = [];

    [ObservableProperty]
    public partial bool IsOutlinePanelOpen { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PdfRect> SearchHighlights { get; private set; } = [];

    [ObservableProperty]
    public partial Guid? SelectedTabId { get; private set; }

    [ObservableProperty]
    public partial bool IsThumbnailPanelOpen { get; set; }

    [ObservableProperty]
    public partial bool IsSearchPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTabBar))]
    public partial int TabCount { get; private set; }

    public ReaderViewModel(
        IDocumentTabService tabService,
        IPdfService pdfService,
        IRecentFilesService recentFilesService,
        ITabCloseService tabCloseService,
        IAnnotationStore annotationStore,
        IEditSaveService editSaveService,
        DocumentCollectionViewModel documentCollectionViewModel,
        IUserSettingsService settingsService,
        BackgroundTaskSupervisor backgroundTasks,
        AppNavigation navigation,
        UiHostContext uiHost)
    {
        _tabService = tabService;
        _pdfService = pdfService;
        _recentFilesService = recentFilesService;
        _tabCloseService = tabCloseService;
        _annotationStore = annotationStore;
        _editSaveService = editSaveService;
        _documentCollectionViewModel = documentCollectionViewModel;
        _settingsService = settingsService;
        _backgroundTasks = backgroundTasks;
        _navigation = navigation;
        _uiHost = uiHost;
        _gpuTileCache.EntryEvicted += OnGpuTileEvicted;
        _thumbnailCache.EntryEvicted += OnThumbnailEvicted;
        _semanticPageCache.EntryEvicted += OnSemanticPageEvicted;
        _tabService.StateChanged += OnSessionStateChanged;
        _tabService.TabsChanged += OnTabsChanged;
        SyncTabItems();
    }

    private void OnGpuTileEvicted(object? sender, CacheEviction<RenderKey> eviction)
    {
        var operationId = TelemetryOperation.NextId();
        ElliePdfEventSource.Log.CacheEvicted(operationId, eviction.ByteCount, (int)eviction.Reason);
        ElliePdfEventSource.Log.CacheBytes(operationId, _gpuTileCache.ResidentBytes);
    }

    private void OnThumbnailEvicted(object? sender, CacheEviction<ThumbnailCacheKey> eviction)
    {
        if (_thumbnailDocumentId == eviction.Key.DocumentId &&
            (uint)eviction.Key.PageIndex < (uint)PageThumbnails.Count)
        {
            PageThumbnails[eviction.Key.PageIndex].Thumbnail = null;
        }

        var operationId = TelemetryOperation.NextId();
        ElliePdfEventSource.Log.CacheEvicted(operationId, eviction.ByteCount, (int)eviction.Reason);
        ElliePdfEventSource.Log.CacheBytes(operationId, _thumbnailCache.ResidentBytes);
    }

    private void OnSemanticPageEvicted(object? sender, CacheEviction<SemanticCacheKey> eviction)
    {
        _semanticCacheKeys.Remove(eviction.Key);
        if (_semanticControllers.TryGetValue(eviction.Key.DocumentId, out var controller))
        {
            controller.EvictPage(eviction.Key.PageIndex);
        }

        if (_semanticDocument?.EngineSession.DocumentId.Value == eviction.Key.DocumentId)
        {
            if ((uint)eviction.Key.PageIndex < (uint)ContinuousPages.Count)
            {
                ContinuousPages[eviction.Key.PageIndex].SemanticPage = null;
            }

            if (_tabService.CurrentPageIndex == eviction.Key.PageIndex)
            {
                CurrentSemanticPage = null;
            }
        }

        var operationId = TelemetryOperation.NextId();
        ElliePdfEventSource.Log.CacheEvicted(operationId, eviction.ByteCount, (int)eviction.Reason);
        ElliePdfEventSource.Log.CacheBytes(operationId, _semanticPageCache.ResidentBytes);
    }

    public bool ShowRecentFiles => !HasDocument && RecentFiles.Count > 0;

    public bool ShowEmptyState => !HasDocument;

    public bool ShowTabBar => TabCount > 1;

    public bool IsSidebarOpen => IsThumbnailPanelOpen || IsOutlinePanelOpen || IsSearchPanelOpen;

    public bool IsReadMode => ToolMode == ReaderToolMode.Read;

    public bool IsEditMode => ToolMode == ReaderToolMode.Edit;

    public bool ShowReadToolbar => HasDocument && IsReadMode;

    public bool ShowEditToolbar => HasDocument && IsEditMode;

    private DocumentContext? FindDocumentContext(PdfDocumentSession document) =>
        _tabService.Tabs
            .FirstOrDefault(tab => ReferenceEquals(tab.OpenSession, document))
            ?.Context;

    private Task<T> RunDocumentRenderAsync<T>(
        PdfDocumentSession document,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var context = FindDocumentContext(document);
        return context is null
            ? operation(cancellationToken)
            : context.RunRenderAsync(operation, cancellationToken);
    }

    private Task<T> RunDocumentOtherAsync<T>(
        PdfDocumentSession document,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var context = FindDocumentContext(document);
        return context is null
            ? operation(cancellationToken)
            : context.RunOtherAsync(operation, cancellationToken);
    }

    private RenderGeneration AdvanceDocumentRenderGeneration(PdfDocumentSession document)
    {
        FindDocumentContext(document)?.AdvanceRenderGeneration();
        return _pdfService.AdvanceRenderGeneration(document);
    }

    public bool IsLabsEnabled => _settingsService.Settings.EnableLabs;

    public bool IsContinuousView => ViewMode == PdfReaderViewMode.Continuous;

    public bool IsSinglePageView => ViewMode == PdfReaderViewMode.SinglePage;

    public bool ShowContinuousViewer => HasDocument && IsReadMode && IsContinuousView;

    public bool ShowSinglePageViewer => HasDocument && (IsEditMode || IsSinglePageView);

    public event EventHandler<int>? PageNavigationRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadMode))]
    [NotifyPropertyChangedFor(nameof(IsEditMode))]
    [NotifyPropertyChangedFor(nameof(ShowReadToolbar))]
    [NotifyPropertyChangedFor(nameof(ShowEditToolbar))]
    [NotifyPropertyChangedFor(nameof(ShowContinuousViewer))]
    [NotifyPropertyChangedFor(nameof(ShowSinglePageViewer))]
    public partial ReaderToolMode ToolMode { get; private set; } = ReaderToolMode.Read;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContinuousView))]
    [NotifyPropertyChangedFor(nameof(IsSinglePageView))]
    [NotifyPropertyChangedFor(nameof(ShowContinuousViewer))]
    [NotifyPropertyChangedFor(nameof(ShowSinglePageViewer))]
    public partial PdfReaderViewMode ViewMode { get; private set; } = PdfReaderViewMode.Continuous;

    [ObservableProperty]
    public partial ImageSource? PageImage { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; private set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial string SearchStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial SemanticPageSnapshot? CurrentSemanticPage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    public partial PdfPermissions? CurrentPermissions { get; private set; }

    [ObservableProperty]
    public partial DocumentPropertiesViewModel? DocumentProperties { get; private set; }

    public bool CanCopy => CurrentPermissions?.CanCopy == true;

    [ObservableProperty]
    public partial bool IsInkModeEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectToolActive))]
    [NotifyPropertyChangedFor(nameof(IsInkToolActive))]
    [NotifyPropertyChangedFor(nameof(IsTextToolActive))]
    [NotifyPropertyChangedFor(nameof(IsSignatureToolActive))]
    [NotifyPropertyChangedFor(nameof(IsEraserToolActive))]
    public partial ReaderEditTool ActiveEditTool { get; private set; } = ReaderEditTool.Select;

    public bool IsSelectToolActive => ActiveEditTool == ReaderEditTool.Select;

    public bool IsInkToolActive => ActiveEditTool == ReaderEditTool.Ink;

    public bool IsTextToolActive => ActiveEditTool == ReaderEditTool.Text;

    public bool IsSignatureToolActive => ActiveEditTool == ReaderEditTool.Signature;

    public bool IsEraserToolActive => ActiveEditTool == ReaderEditTool.Eraser;

    [ObservableProperty]
    public partial string InkColorHex { get; private set; } = "#000000";

    [ObservableProperty]
    public partial double InkThickness { get; private set; } = 2;

    [ObservableProperty]
    public partial int PagePixelWidth { get; private set; }

    [ObservableProperty]
    public partial int PagePixelHeight { get; private set; }

    [ObservableProperty]
    public partial float PageWidthPoints { get; private set; }

    [ObservableProperty]
    public partial float PageHeightPoints { get; private set; }

    public bool HasDocument => _tabService.ActiveDocument is not null;

    // The benchmark driver reads this aggregate only. It contains no page, image,
    // path, or document-derived data and remains unavailable outside this assembly.
    internal long BenchmarkGpuTileCacheBytes => _gpuTileCache.ResidentBytes;

    internal long BenchmarkThumbnailCacheBytes => _thumbnailCache.ResidentBytes;
    internal long BenchmarkGeometryCacheBytes => _semanticPageCache.ResidentBytes;

    // Set only when the first readable page pixels have replaced the placeholder.
    // This is deliberately measured from document open, not process startup or
    // benchmark-driver readiness, so the standalone benchmark cannot gate on a
    // startup proxy.
    internal double? BenchmarkFirstPagePresentedMilliseconds { get; private set; }

    public bool CanSave => _tabService.ActiveTab?.IsDirty == true;

    public string DocumentTitle =>
        _tabService.ActiveFileName ?? AppResources.Get("Reader_NoDocument");

    public string PageLabel
    {
        get
        {
            var document = _tabService.ActiveDocument;
            if (document is null || document.PageCount == 0)
            {
                return AppResources.Get("Reader_PageUnavailable");
            }

            return AppResources.Format(
                "Reader_PageOfCount",
                _tabService.CurrentPageIndex + 1,
                document.PageCount);
        }
    }

    public string CurrentPageNumberText => HasDocument
        ? (_tabService.CurrentPageIndex + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)
        : string.Empty;

    public string ZoomLabel => $"{Math.Round(EffectiveZoomScale * 100)}%";

    public double EffectiveZoomScale => ShowContinuousViewer && _continuousScale > 0
        ? _continuousScale * 72d / 96d
        : ResolveRenderScale() * 72d / 96d;

    public double DisplayScale => PageWidthPoints > 0 ? PagePixelWidth / PageWidthPoints : 1.0;

    public double RasterizationScale
    {
        get => _rasterizationScale;
        set
        {
            if (!double.IsFinite(value) || value <= 0 || Math.Abs(_rasterizationScale - value) < 0.001)
            {
                return;
            }

            _rasterizationScale = value;
            ObserveBackground(RefreshRenderedPagesAsync(navigateToCurrentPage: true), "reader-dpi-render");
        }
    }

    public RenderMode RenderMode
    {
        get => _renderMode;
        set
        {
            if (_renderMode == value)
            {
                return;
            }

            _renderMode = value;
            ObserveBackground(RefreshRenderedPagesAsync(navigateToCurrentPage: true), "reader-theme-render");
        }
    }

    public void ApplyRenderMemoryPressure(RenderMemoryPressureLevel pressure)
    {
        var budgets = RenderCacheBudgets.Default.ApplyMemoryPressure(pressure);
        _gpuTileCache.SetBudget(budgets.GpuTileBudgetBytes, CacheEvictionReason.MemoryPressure);
        _thumbnailCache.SetBudget(budgets.ThumbnailBudgetBytes, CacheEvictionReason.MemoryPressure);
        _semanticPageCache.SetBudget(budgets.MetadataBudgetBytes, CacheEvictionReason.MemoryPressure);
        if (_pdfService is PdfService service)
        {
            service.ApplyRenderMemoryPressure(pressure);
        }
    }

    public PageOverlayState CurrentOverlay
    {
        get
        {
            var tab = _tabService.ActiveTab;
            if (tab is null)
            {
                return new PageOverlayState();
            }

            return _annotationStore.GetPageOverlay(tab.Id, _tabService.CurrentPageIndex);
        }
    }

    public double ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            if (Math.Abs(_viewportWidth - value) < 1)
            {
                return;
            }

            _viewportWidth = Math.Max(200, value);
            if (_tabService.ZoomMode is PdfZoomMode.FitWidth or PdfZoomMode.FitPage)
            {
                ObserveBackground(RefreshRenderedPagesAsync(), "reader-viewport-width-render");
            }

            OnPropertyChanged(nameof(ZoomLabel));
        }
    }

    public double ViewportHeight
    {
        get => _viewportHeight;
        set
        {
            if (Math.Abs(_viewportHeight - value) < 1)
            {
                return;
            }

            _viewportHeight = Math.Max(200, value);
            if (_tabService.ZoomMode is PdfZoomMode.FitPage)
            {
                ObserveBackground(RefreshRenderedPagesAsync(), "reader-viewport-height-render");
            }

            OnPropertyChanged(nameof(ZoomLabel));
        }
    }

    public async Task LoadDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        DismissStatus();

        try
        {
            await _tabService.OpenOrActivateTabAsync(path, cancellationToken);
            ApplyDefaultZoomMode();
            SelectedTabId = _tabService.ActiveTabId;
            SyncTabItems();
            NotifyDocumentChanged();
            await RefreshRenderedPagesAsync(navigateToCurrentPage: true);
            await RefreshRecentFilesAsync(cancellationToken);
            SetStatus(AppResources.Format("Reader_StatusOpened", Path.GetFileName(path)), InfoBarSeverity.Success);
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            SetStatus(AppResources.Get("Reader_StatusOpenCancelled"), InfoBarSeverity.Informational);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadFilesAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        foreach (var filePath in filePaths)
        {
            await _tabService.OpenOrActivateTabAsync(filePath, cancellationToken);
        }

        SelectedTabId = _tabService.ActiveTabId;
        SyncTabItems();
        ApplyDefaultZoomMode();
        NotifyDocumentChanged();
        await RefreshRenderedPagesAsync(navigateToCurrentPage: true);
        await RefreshRecentFilesAsync(cancellationToken);
    }

    public async Task ActivateTabAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _tabService.ActivateTabAsync(tabId, cancellationToken);
            SelectedTabId = _tabService.ActiveTabId;
            SyncTabItems();
            NotifyDocumentChanged();
            await RefreshRenderedPagesAsync(navigateToCurrentPage: true);
            if (IsThumbnailPanelOpen)
            {
                await EnsureThumbnailsLoadedAsync();
            }
        }
        catch (OperationCanceledException)
        {
            SelectedTabId = _tabService.ActiveTabId;
            SyncTabItems();
            SetStatus(AppResources.Get("Reader_StatusOpenCancelled"), InfoBarSeverity.Informational);
        }
    }

    public async Task<bool> TryCloseTabAsync(Guid tabId)
    {
        try
        {
            if (!await _tabCloseService.TryCloseTabAsync(tabId))
            {
                return false;
            }

            await ApplyTabStateAfterCloseAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal user outcome (for example, cancelling a
            // save prompt). The tab must remain open and the async event handler
            // must never observe the exception.
            SetStatus(AppResources.Get("Reader_StatusCloseCancelled"), InfoBarSeverity.Informational);
            return false;
        }
        catch (AtomicSaveConflictException)
        {
            SetStatus(AppResources.Get("Reader_StatusSaveConflict"), InfoBarSeverity.Error);
            return false;
        }
        catch (IOException exception)
        {
            SetStatus(AppResources.Format("Reader_StatusSaveFailed", exception.Message), InfoBarSeverity.Error);
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            SetStatus(AppResources.Format("Reader_StatusSaveFailed", exception.Message), InfoBarSeverity.Error);
            return false;
        }
    }

    public async Task ApplyTabStateAfterCloseAsync()
    {
        SelectedTabId = _tabService.ActiveTabId;
        SyncTabItems();
        NotifyDocumentChanged();
        await RefreshRenderedPagesAsync(navigateToCurrentPage: true);
        if (IsThumbnailPanelOpen)
        {
            await EnsureThumbnailsLoadedAsync();
        }

        await RefreshRecentFilesAsync();
    }

    public void ClosePanels()
    {
        IsThumbnailPanelOpen = false;
        IsSearchPanelOpen = false;
        IsOutlinePanelOpen = false;
    }

    public async Task EnsureThumbnailsLoadedAsync()
    {
        if (!HasDocument)
        {
            PageThumbnails.Clear();
            return;
        }

        await LoadPageThumbnailsAsync();
    }

    partial void OnIsThumbnailPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarOpen));
        if (value)
        {
            IsOutlinePanelOpen = false;
            IsSearchPanelOpen = false;
            ObserveBackground(EnsureThumbnailsLoadedAsync(), "reader-thumbnail-load");
        }
    }

    partial void OnIsOutlinePanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarOpen));
        if (value)
        {
            IsThumbnailPanelOpen = false;
            IsSearchPanelOpen = false;
            ObserveBackground(EnsureOutlineLoadedAsync(), "reader-outline-load");
        }
    }

    [RelayCommand]
    private void ToggleThumbnailPanel()
    {
        if (IsThumbnailPanelOpen)
        {
            IsThumbnailPanelOpen = false;
            return;
        }

        IsSearchPanelOpen = false;
        IsThumbnailPanelOpen = true;
    }

    [RelayCommand]
    private void ToggleSearchPanel()
    {
        if (IsSearchPanelOpen)
        {
            IsSearchPanelOpen = false;
            return;
        }

        IsThumbnailPanelOpen = false;
        IsOutlinePanelOpen = false;
        IsSearchPanelOpen = true;
    }

    [RelayCommand]
    private void ToggleOutlinePanel()
    {
        if (IsOutlinePanelOpen)
        {
            IsOutlinePanelOpen = false;
            return;
        }

        IsThumbnailPanelOpen = false;
        IsSearchPanelOpen = false;
        IsOutlinePanelOpen = true;
    }

    [RelayCommand]
    private void GoToOutlineItem(OutlineItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        GoToPage(item.PageIndex);
        ClosePanels();
    }

    [RelayCommand]
    private void GoToThumbnailPage(PageThumbnailViewModel? thumbnail)
    {
        if (thumbnail is null)
        {
            return;
        }

        GoToPage(thumbnail.PageIndex);
        ClosePanels();
    }

    public async Task RefreshFromSessionAsync()
    {
        NotifyDocumentChanged();
        await RefreshRenderedPagesAsync(navigateToCurrentPage: true);
    }

    partial void OnIsSearchPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarOpen));
    }

    [RelayCommand]
    private async Task UseContinuousViewAsync()
    {
        if (IsContinuousView)
        {
            PageNavigationRequested?.Invoke(this, _tabService.CurrentPageIndex);
            return;
        }

        ViewMode = PdfReaderViewMode.Continuous;
        await RenderContinuousPagesAsync();
        PageNavigationRequested?.Invoke(this, _tabService.CurrentPageIndex);
    }

    [RelayCommand]
    private void UseSinglePageView()
    {
        if (IsSinglePageView)
        {
            return;
        }

        ViewMode = PdfReaderViewMode.SinglePage;
        ObserveBackground(RenderCurrentPageAsync(), "reader-single-page-render");
    }

    public void GoToPage(int pageIndex)
    {
        _tabService.CurrentPageIndex = pageIndex;
        if (ShowContinuousViewer)
        {
            ApplyContinuousCurrentPage(_tabService.CurrentPageIndex);
            PageNavigationRequested?.Invoke(this, _tabService.CurrentPageIndex);
        }
        else
        {
            ObserveBackground(RenderCurrentPageAsync(), "reader-page-navigation-render");
        }
    }

    public void GoToPageNumber(int oneBasedPageNumber)
    {
        if (!HasDocument)
        {
            return;
        }

        GoToPage(Math.Clamp(oneBasedPageNumber - 1, 0, Math.Max(0, DocumentPageCount - 1)));
    }

    public void SetCurrentPageFromContinuousScroll(int pageIndex)
    {
        if (!ShowContinuousViewer || pageIndex == _tabService.CurrentPageIndex)
        {
            return;
        }

        _isTrackingContinuousScroll = true;
        try
        {
            _tabService.CurrentPageIndex = pageIndex;
        }
        finally
        {
            _isTrackingContinuousScroll = false;
        }

        ApplyContinuousCurrentPage(_tabService.CurrentPageIndex);
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (!HasDocument)
        {
            return;
        }

        GoToPage(_tabService.CurrentPageIndex - 1);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!HasDocument)
        {
            return;
        }

        GoToPage(_tabService.CurrentPageIndex + 1);
    }

    [RelayCommand]
    private void ZoomIn()
    {
        var oldScale = ResolveRenderScale();
        _tabService.ZoomMode = PdfZoomMode.Custom;
        _tabService.ZoomScale *= 1.25;
        ScaleSinglePagePlaceholder(oldScale, ResolveRenderScale());
        NotifyZoomChanged();
        ObserveBackground(RefreshRenderedPagesAsync(navigateToCurrentPage: true), "reader-zoom-in-render");
    }

    [RelayCommand]
    private void ZoomOut()
    {
        var oldScale = ResolveRenderScale();
        _tabService.ZoomMode = PdfZoomMode.Custom;
        _tabService.ZoomScale /= 1.25;
        ScaleSinglePagePlaceholder(oldScale, ResolveRenderScale());
        NotifyZoomChanged();
        ObserveBackground(RefreshRenderedPagesAsync(navigateToCurrentPage: true), "reader-zoom-out-render");
    }

    public void ApplyZoomFactor(double factor)
    {
        if (!HasDocument || !double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        var oldScale = ResolveRenderScale();
        var targetScale = Math.Clamp(oldScale * factor, 0.1 * 96.0 / 72.0, 64.0 * 96.0 / 72.0);
        _tabService.ZoomMode = PdfZoomMode.Custom;
        _tabService.ZoomScale = targetScale * 72.0 / 96.0;
        ScaleSinglePagePlaceholder(oldScale, ResolveRenderScale());
        NotifyZoomChanged();
        ObserveBackground(
            RefreshRenderedPagesAsync(navigateToCurrentPage: true),
            "reader-pinch-zoom-render");
    }

    [RelayCommand]
    private void ZoomActualSize()
    {
        var oldScale = ResolveRenderScale();
        _tabService.ZoomMode = PdfZoomMode.ActualSize;
        _tabService.ZoomScale = 1.0;
        ScaleSinglePagePlaceholder(oldScale, ResolveRenderScale());
        NotifyZoomChanged();
        ObserveBackground(RefreshRenderedPagesAsync(navigateToCurrentPage: true), "reader-fit-width-render");
    }

    [RelayCommand]
    private void ZoomFitWidth()
    {
        var oldScale = ResolveRenderScale();
        _tabService.ZoomMode = PdfZoomMode.FitWidth;
        ScaleSinglePagePlaceholder(oldScale, ResolveRenderScale());
        NotifyZoomChanged();
        ObserveBackground(RefreshRenderedPagesAsync(navigateToCurrentPage: true), "reader-fit-page-render");
    }

    [RelayCommand]
    private void ZoomFitPage()
    {
        var oldScale = ResolveRenderScale();
        _tabService.ZoomMode = PdfZoomMode.FitPage;
        ScaleSinglePagePlaceholder(oldScale, ResolveRenderScale());
        NotifyZoomChanged();
        ObserveBackground(RefreshRenderedPagesAsync(navigateToCurrentPage: true), "reader-actual-size-render");
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var document = _tabService.ActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(SearchQuery))
        {
            _searchCts?.Cancel();
            SearchStatus = string.Empty;
            _searchMatches.Clear();
            SearchResults.Clear();
            _activeSearchMatchIndex = -1;
            UpdateSearchHighlights();
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var controller = GetSemanticController(document);
        _searchMatches.Clear();
        SearchResults.Clear();
        _activeSearchMatchIndex = -1;
        UpdateSearchHighlights();
        SearchStatus = AppResources.Get("Reader_Searching");

        async Task ConsumeResultsAsync(CancellationToken linkedToken)
        {
            await foreach (var result in controller.SearchAsync(
                SearchQuery.Trim(),
                MatchCase,
                wholeWord: false,
                linkedToken))
            {
                linkedToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(document, _tabService.ActiveDocument))
                {
                    return;
                }

                _searchMatches.Add(ToTextMatch(result));
                SearchResults.Add(new SearchResultItemViewModel(result));
                if (_activeSearchMatchIndex < 0)
                {
                    _activeSearchMatchIndex = 0;
                    GoToPage(result.PageIndex);
                    UpdateSearchHighlights();
                }
                SearchStatus = AppResources.FormatPlural(
                    "Reader_SearchProgressOne",
                    "Reader_SearchProgressMany",
                    _searchMatches.Count,
                    _searchMatches.Count);
            }
        }

        try
        {
            var context = FindDocumentContext(document);
            context?.AdvanceSearchGeneration();
            if (context is null)
            {
                await ConsumeResultsAsync(token);
            }
            else
            {
                await context.RunSearchAsync(ConsumeResultsAsync, token);
            }

            if (!ReferenceEquals(document, _tabService.ActiveDocument))
            {
                return;
            }

            if (_searchMatches.Count == 0)
            {
                _activeSearchMatchIndex = -1;
                SearchStatus = AppResources.Get("Reader_SearchNoMatches");
                UpdateSearchHighlights();
                return;
            }

            SearchStatus = FormatSearchMatch(_activeSearchMatchIndex + 1);
        }
        catch (OperationCanceledException)
        {
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task NextSearchMatchAsync()
    {
        if (_searchMatches.Count == 0)
        {
            await SearchAsync();
            return;
        }

        _activeSearchMatchIndex = (_activeSearchMatchIndex + 1) % _searchMatches.Count;
        var match = _searchMatches[_activeSearchMatchIndex];
        GoToPage(match.PageIndex);
        SearchStatus = FormatSearchMatch(_activeSearchMatchIndex + 1);
    }

    [RelayCommand]
    private async Task PreviousSearchMatchAsync()
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        _activeSearchMatchIndex = (_activeSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        var match = _searchMatches[_activeSearchMatchIndex];
        GoToPage(match.PageIndex);
        SearchStatus = FormatSearchMatch(_activeSearchMatchIndex + 1);
    }

    [RelayCommand]
    private void GoToSearchResult(SearchResultItemViewModel? item)
    {
        if (item is null) return;
        var index = SearchResults.IndexOf(item);
        if (index < 0 || index >= _searchMatches.Count) return;
        _activeSearchMatchIndex = index;
        GoToPage(item.Result.PageIndex);
        SearchStatus = FormatSearchMatch(index + 1);
    }

    private static TextMatch ToTextMatch(ContractSearchResult result) => new(
        result.PageIndex,
        result.CharIndex,
        result.MatchLength,
        result.Context,
        result.HighlightRects.Select(static rectangle => new PdfRect(
            checked((float)rectangle.Left),
            checked((float)rectangle.Top),
            checked((float)rectangle.Right),
            checked((float)rectangle.Bottom))).ToArray());

    private string FormatSearchMatch(int currentMatch) => AppResources.FormatPlural(
        "Reader_SearchMatchOne",
        "Reader_SearchMatchMany",
        _searchMatches.Count,
        currentMatch,
        _searchMatches.Count);

    public async Task ActivateSemanticLinkAsync(
        SemanticLinkSnapshot link,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!link.IsSafeToActivate)
        {
            SetStatus(link.BlockedReason ?? AppResources.Get("Reader_LinkBlocked"), InfoBarSeverity.Warning);
            return;
        }

        if (link.Kind == PdfLinkKind.Page && link.TargetPageIndex is int pageIndex)
        {
            GoToPage(pageIndex);
            return;
        }

        if (link.Kind != PdfLinkKind.Uri
            || PdfExternalLinkPolicy.Evaluate(link.Uri, out var safeUri) != ExternalLinkDecision.Allowed
            || safeUri is null)
        {
            SetStatus(AppResources.Get("Reader_LinkBlocked"), InfoBarSeverity.Warning);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!await Windows.System.Launcher.LaunchUriAsync(safeUri))
        {
            SetStatus(AppResources.Get("Reader_LinkLaunchFailed"), InfoBarSeverity.Warning);
        }
    }

    public async Task CommitFormValueAsync(
        SemanticFormSnapshot form,
        FormValue value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(value);
        var tab = _tabService.ActiveTab;
        var document = tab?.OpenSession;
        if (tab is null || document is null || form.PageIndex >= document.PageCount)
        {
            return;
        }

        var controller = GetSemanticController(document);
        try
        {
            var decision = await RunDocumentOtherAsync(
                document,
                token => controller.UpdateFormAsync(form, value, token).AsTask(),
                cancellationToken);
            if (!decision.Applied)
            {
                SetStatus(decision.Reason ?? AppResources.Get("Reader_FormUpdateBlocked"), InfoBarSeverity.Warning);
                return;
            }

            tab.MarkContentChanged();
            _annotationStore.SetFormRecoveryEdit(
                tab.Id,
                ToRecoveryEdit(form, value),
                tab.State.ContentRevision);
            _annotationStore.ScheduleRecoveryCheckpoint(
                tab.Id,
                tab.FilePath,
                tab.State.ContentRevision,
                document.SourceVersion);
            SyncTabItems();
            OnPropertyChanged(nameof(CanSave));
            if (controller.TryGetPage(form.PageIndex, out var updated) && updated is not null)
            {
                PublishSemanticPage(document, form.PageIndex, updated);
            }
            SetStatus(AppResources.Get("Reader_FormUpdated"), InfoBarSeverity.Success);
            await RefreshRenderedPagesAsync();
        }
        catch (UnauthorizedAccessException exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Warning);
        }
        catch (InvalidOperationException exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    public async Task InvokePushButtonAsync(
        SemanticFormSnapshot form,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        var tab = _tabService.ActiveTab;
        var document = tab?.OpenSession;
        if (tab is null || document is null || form.PageIndex >= document.PageCount)
        {
            return;
        }

        var controller = GetSemanticController(document);
        try
        {
            var decision = await RunDocumentOtherAsync(
                document,
                token => controller.InvokePushButtonAsync(form, token).AsTask(),
                cancellationToken);
            if (!decision.Invoked)
            {
                SetStatus(decision.Reason ?? AppResources.Get("Reader_FormUpdateBlocked"), InfoBarSeverity.Warning);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Warning);
        }
        catch (InvalidOperationException exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    public async Task<DocumentPropertiesViewModel?> LoadDocumentPropertiesAsync(
        CancellationToken cancellationToken = default)
    {
        var document = _tabService.ActiveDocument;
        if (document is null) return null;
        var snapshot = await RunDocumentOtherAsync(
            document,
            token => GetSemanticController(document).GetDocumentSnapshotAsync(token).AsTask(),
            cancellationToken);
        var metadata = snapshot.Metadata;
        var loadedPage = snapshot.LoadedPages.FirstOrDefault(
            candidate => candidate.Metadata.PageIndex == _tabService.CurrentPageIndex);
        (double Width, double Height)? pageSize = loadedPage is null
            ? null
            : (loadedPage.Metadata.SizeInPoints.Width, loadedPage.Metadata.SizeInPoints.Height);
        if (pageSize is null && metadata.PageCount > 0)
        {
            var size = await RunDocumentOtherAsync(
                document,
                token => _pdfService.GetPageSizeAsync(document, _tabService.CurrentPageIndex, token),
                cancellationToken);
            pageSize = (size.Width, size.Height);
        }

        var yes = AppResources.Get("Common_Yes");
        var no = AppResources.Get("Common_No");
        static string Flag(bool value, string yesValue, string noValue) => value ? yesValue : noValue;
        var permissions = snapshot.Permissions;
        DocumentProperties = new DocumentPropertiesViewModel(
            metadata.Title ?? AppResources.Get("Common_NotAvailable"),
            metadata.Author ?? AppResources.Get("Common_NotAvailable"),
            metadata.Subject ?? AppResources.Get("Common_NotAvailable"),
            metadata.Creator ?? AppResources.Get("Common_NotAvailable"),
            metadata.PdfVersion ?? AppResources.Get("Common_NotAvailable"),
            metadata.PageCount.ToString(System.Globalization.CultureInfo.CurrentCulture),
            pageSize is { } dimensions
                ? AppResources.Format("Reader_PropertiesPageSizeValue", dimensions.Width, dimensions.Height)
                : AppResources.Get("Common_NotAvailable"),
            metadata.IsEncrypted
                ? AppResources.Get("Reader_PropertiesEncrypted")
                : AppResources.Get("Reader_PropertiesNotEncrypted"),
            AppResources.Format(
                "Reader_PropertiesPermissionsValue",
                Flag(permissions.CanPrint, yes, no),
                Flag(permissions.CanCopy, yes, no),
                Flag(permissions.CanModify, yes, no),
                Flag(permissions.CanFillForms, yes, no),
                Flag(permissions.CanAssemble, yes, no)),
            metadata.HasForms
                ? AppResources.Get("Common_Yes")
                : AppResources.Get("Common_No"),
            metadata.HasOutline
                ? AppResources.Get("Common_Yes")
                : AppResources.Get("Common_No"));
        return DocumentProperties;
    }

    private SemanticReaderController GetSemanticController(PdfDocumentSession document)
    {
        var id = document.EngineSession.DocumentId.Value;
        if (_semanticControllers.TryGetValue(id, out var controller)) return controller;
        controller = new SemanticReaderController(document.EngineSession, ownsSession: false);
        _semanticControllers[id] = controller;
        return controller;
    }

    private void PrepareSemanticDocument(PdfDocumentSession document)
    {
        if (ReferenceEquals(_semanticDocument, document)) return;
        _semanticCts?.Cancel();
        _semanticCts?.Dispose();
        _semanticCts = new CancellationTokenSource();
        _semanticDocument = document;
        CurrentSemanticPage = null;
        CurrentPermissions = null;
        DocumentProperties = null;
        _searchCts?.Cancel();
        _searchMatches.Clear();
        SearchResults.Clear();
        _activeSearchMatchIndex = -1;
        SearchStatus = string.Empty;
        UpdateSearchHighlights();
        ObserveBackground(LoadPermissionsAsync(document, _semanticCts.Token), "reader-semantic-permissions");
    }

    private async Task LoadPermissionsAsync(PdfDocumentSession document, CancellationToken cancellationToken)
    {
        try
        {
            var permissions = await RunDocumentOtherAsync(
                document,
                token => _pdfService.GetPermissionsAsync(document, token),
                cancellationToken);
            if (!ReferenceEquals(document, _semanticDocument)) return;
            CurrentPermissions = permissions;
            foreach (var page in ContinuousPages)
            {
                page.CanCopy = permissions.CanCopy;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void QueueSemanticPage(PdfDocumentSession document, int pageIndex)
    {
        PrepareSemanticDocument(document);
        var documentId = document.EngineSession.DocumentId.Value;
        var cacheKey = new SemanticCacheKey(documentId, pageIndex);
        if (_semanticPageCache.TryGet(cacheKey, out var resident) && resident is not null)
        {
            ElliePdfEventSource.Log.CacheHit(TelemetryOperation.NextId(), EstimateSemanticPageBytes(resident));
            PublishSemanticPage(document, pageIndex, resident, updateCache: false);
            return;
        }

        ElliePdfEventSource.Log.CacheMiss(TelemetryOperation.NextId());

        var controller = GetSemanticController(document);
        if (controller.TryGetPage(pageIndex, out var cached) && cached is not null)
        {
            PublishSemanticPage(document, pageIndex, cached);
            return;
        }

        var key = (documentId, pageIndex);
        if (_semanticLoads.ContainsKey(key)) return;
        var task = LoadSemanticPageAsync(document, pageIndex, key, _semanticCts?.Token ?? CancellationToken.None);
        _semanticLoads[key] = task;
        ObserveBackground(task, $"reader-semantic-page-{pageIndex}");
    }

    private async Task<SemanticPageSnapshot> LoadSemanticPageAsync(
        PdfDocumentSession document,
        int pageIndex,
        (Guid DocumentId, int PageIndex) key,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await RunDocumentOtherAsync(
                document,
                token => GetSemanticController(document).GetPageAsync(pageIndex, token).AsTask(),
                cancellationToken);
            PublishSemanticPage(document, pageIndex, page);
            return page;
        }
        finally
        {
            _semanticLoads.Remove(key);
        }
    }

    private void PublishSemanticPage(
        PdfDocumentSession document,
        int pageIndex,
        SemanticPageSnapshot page,
        bool updateCache = true)
    {
        if (updateCache)
        {
            var key = new SemanticCacheKey(document.EngineSession.DocumentId.Value, pageIndex);
            if (_semanticPageCache.Set(key, page, EstimateSemanticPageBytes(page)))
            {
                _semanticCacheKeys.Add(key);
                ElliePdfEventSource.Log.CacheBytes(TelemetryOperation.NextId(), _semanticPageCache.ResidentBytes);
            }
            else if (_semanticPageCache.TryGet(key, out var retained) && retained is not null)
            {
                page = retained;
            }
            else
            {
                GetSemanticController(document).EvictPage(pageIndex);
                return;
            }
        }

        if (!ReferenceEquals(document, _semanticDocument)) return;
        if ((uint)pageIndex < (uint)ContinuousPages.Count)
        {
            ContinuousPages[pageIndex].SemanticPage = page;
            ContinuousPages[pageIndex].CanCopy = CanCopy;
        }
        if (pageIndex == _tabService.CurrentPageIndex)
        {
            CurrentSemanticPage = page;
        }
    }

    private static long EstimateSemanticPageBytes(SemanticPageSnapshot page)
    {
        ArgumentNullException.ThrowIfNull(page);
        static long TextBytes(string? value) => value is null ? 0 : 24L + (2L * value.Length);

        var bytes = 512L + TextBytes(page.Metadata.Label) + TextBytes(page.Text.Text);
        foreach (var span in page.Text.Spans)
        {
            bytes = checked(bytes + 96L + TextBytes(span.Text));
        }

        foreach (var link in page.Links)
        {
            bytes = checked(bytes + 160L + TextBytes(link.Uri) + TextBytes(link.Name) + TextBytes(link.BlockedReason));
        }

        foreach (var form in page.Forms)
        {
            bytes = checked(bytes + 224L + TextBytes(form.FieldName) + TextBytes(form.UnsupportedReason));
            bytes = checked(bytes + TextBytes(form.Value.Text));
            foreach (var choice in form.Value.Choices)
            {
                bytes = checked(bytes + 24L + TextBytes(choice));
            }
            foreach (var option in form.Options)
            {
                bytes = checked(bytes + 24L + TextBytes(option));
            }
        }

        if (page.Selection is { } selection)
        {
            bytes = checked(bytes + 128L + TextBytes(selection.Text) + (selection.Segments.Length * 48L));
        }

        return Math.Max(1L, bytes);
    }

    private static FormRecoveryEdit ToRecoveryEdit(SemanticFormSnapshot form, FormValue value) => new()
    {
        PageIndex = form.PageIndex,
        FieldName = form.FieldName,
        WidgetType = form.Type.ToString(),
        ValueKind = value.Kind.ToString(),
        Text = value.Text,
        Boolean = value.Boolean,
        Choices = [.. value.Choices]
    };

    public void PersistCurrentOverlay(PageOverlayState overlay)
    {
        var tab = _tabService.ActiveTab;
        if (tab is null)
        {
            return;
        }

        tab.MarkContentChanged();
        _annotationStore.SetPageOverlay(
            tab.Id,
            _tabService.CurrentPageIndex,
            overlay,
            tab.State.ContentRevision);
        _annotationStore.ScheduleRecoveryCheckpoint(
            tab.Id,
            tab.FilePath,
            tab.State.ContentRevision,
            tab.Session.SourceVersion);
    }

    [RelayCommand]
    private async Task EnterEditModeAsync()
    {
        if (!IsLabsEnabled)
        {
            SetStatus(AppResources.Get("Reader_StatusEditLabsOnly"), InfoBarSeverity.Informational);
            return;
        }

        var tab = _tabService.ActiveTab;
        if (tab is null)
        {
            SetStatus(AppResources.Get("Reader_StatusOpenBeforeEdit"), InfoBarSeverity.Informational);
            return;
        }

        ClosePanels();
        ActiveEditTool = ReaderEditTool.Select;
        IsInkModeEnabled = false;
        ToolMode = ReaderToolMode.Edit;
        OnPropertyChanged(nameof(CurrentOverlay));
        await RenderCurrentPageAsync();
    }

    [RelayCommand]
    private void ExitEditMode()
    {
        IsInkModeEnabled = false;
        ActiveEditTool = ReaderEditTool.Select;
        ToolMode = ReaderToolMode.Read;
        if (IsContinuousView)
        {
            PageNavigationRequested?.Invoke(this, _tabService.CurrentPageIndex);
        }
    }

    [RelayCommand]
    private void UseSelectTool()
    {
        IsInkModeEnabled = false;
        ActiveEditTool = ReaderEditTool.Select;
    }

    [RelayCommand]
    private void UseInkTool()
    {
        IsInkModeEnabled = true;
        ActiveEditTool = ReaderEditTool.Ink;
    }

    [RelayCommand]
    private void UseTextTool()
    {
        IsInkModeEnabled = false;
        ActiveEditTool = ReaderEditTool.Text;
    }

    [RelayCommand]
    private void UseSignatureTool()
    {
        IsInkModeEnabled = false;
        ActiveEditTool = ReaderEditTool.Signature;
    }

    [RelayCommand]
    private void UseEraserTool()
    {
        IsInkModeEnabled = false;
        ActiveEditTool = ReaderEditTool.Eraser;
    }

    [RelayCommand]
    private void SetInkColor(string? colorHex)
    {
        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            InkColorHex = colorHex;
        }
    }

    [RelayCommand]
    private void SetInkThickness(double thickness) =>
        InkThickness = Math.Clamp(thickness, 1, 12);

    [RelayCommand]
    private void UseThinInk() => InkThickness = 2;

    [RelayCommand]
    private void UseMediumInk() => InkThickness = 5;

    [RelayCommand]
    private void UseThickInk() => InkThickness = 9;

    [RelayCommand]
    private async Task SaveAsync()
    {
        var tab = _tabService.ActiveTab;
        if (tab is null)
        {
            SetStatus(AppResources.Get("Reader_StatusOpenBeforeSave"), InfoBarSeverity.Informational);
            return;
        }

        var confirmed = await ConfirmOverwriteAsync(tab.FilePath);
        if (!confirmed)
        {
            return;
        }

        await SaveToPathAsync(tab, tab.FilePath);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var tab = _tabService.ActiveTab;
        if (tab is null)
        {
            SetStatus(AppResources.Get("Reader_StatusOpenBeforeSave"), InfoBarSeverity.Informational);
            return;
        }

        var picker = new FileSavePicker(GetWindowId())
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(tab.FilePath) + "-edited"
        };
        picker.FileTypeChoices.Add(AppResources.Get("Reader_PdfFileType"), [".pdf"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await SaveToPathAsync(tab, file.Path);
        await _tabService.OpenOrActivateTabAsync(file.Path);
        SelectedTabId = _tabService.ActiveTabId;
        SyncTabItems();
        NotifyDocumentChanged();
        await RefreshRenderedPagesAsync(navigateToCurrentPage: true);
        SetStatus(AppResources.Format("Reader_StatusSavedCopy", Path.GetFileName(file.Path)), InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task OpenOrganizeAsync()
    {
        if (!IsLabsEnabled)
        {
            SetStatus(AppResources.Get("Reader_StatusOrganizeLabsOnly"), InfoBarSeverity.Informational);
            return;
        }

        var tab = _tabService.ActiveTab;
        if (tab is null)
        {
            SetStatus(AppResources.Get("Reader_StatusOpenBeforeOrganize"), InfoBarSeverity.Informational);
            return;
        }

        await _documentCollectionViewModel.ImportDocumentsAsync([tab.FilePath], append: false);
        ToolMode = ReaderToolMode.Read;
        _navigation.RequestWorkspace("organize");
    }

    [RelayCommand]
    private async Task OpenRecentAsync(RecentFileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await LoadDocumentAsync(item.FilePath);
    }

    public void DismissStatus() => IsStatusOpen = false;

    public void Dispose()
    {
        _tabService.StateChanged -= OnSessionStateChanged;
        _tabService.TabsChanged -= OnTabsChanged;
        _semanticPageCache.Clear();
        _semanticPageCache.EntryEvicted -= OnSemanticPageEvicted;
        _gpuTileCache.EntryEvicted -= OnGpuTileEvicted;
        _thumbnailCache.EntryEvicted -= OnThumbnailEvicted;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _continuousRenderCts?.Cancel();
        _continuousRenderCts?.Dispose();
        _singleViewportRenderCts?.Cancel();
        _singleViewportRenderCts?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _semanticCts?.Cancel();
        _semanticCts?.Dispose();
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        foreach (var controller in _semanticControllers.Values)
        {
            _ = controller.DisposeAsync();
        }
        _semanticControllers.Clear();
        _semanticLoads.Clear();
        _semanticCacheKeys.Clear();
        _gpuTileCache.Clear();
        _thumbnailCache.Clear();
        _activeContinuousTileKeys.Clear();
        _firstPageRequests.Clear();
        _firstPagePresentations.Clear();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (_isTrackingContinuousScroll)
        {
            OnPropertyChanged(nameof(PageLabel));
            OnPropertyChanged(nameof(CurrentPageIndex));
            OnPropertyChanged(nameof(CurrentPageNumberText));
            return;
        }

        NotifyDocumentChanged();
        NotifyZoomChanged();
    }

    private void NotifyDocumentChanged()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(CurrentPageNumberText));
        OnPropertyChanged(nameof(DocumentPageCount));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowRecentFiles));
        OnPropertyChanged(nameof(ShowReadToolbar));
        OnPropertyChanged(nameof(ShowEditToolbar));
        OnPropertyChanged(nameof(ShowContinuousViewer));
        OnPropertyChanged(nameof(ShowSinglePageViewer));
        OnPropertyChanged(nameof(CurrentOverlay));
    }

    public async Task RefreshRecentFilesAsync(CancellationToken cancellationToken = default)
    {
        RecentFiles.Clear();
        foreach (var path in await _recentFilesService.GetRecentFilesAsync(cancellationToken))
        {
            RecentFiles.Add(new RecentFileItemViewModel(path));
        }

        OnPropertyChanged(nameof(ShowRecentFiles));
    }

    private Task LoadPageThumbnailsAsync()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
        PageThumbnails.Clear();
        var document = _tabService.ActiveDocument;
        if (document is null)
        {
            _thumbnailDocumentId = null;
            return Task.CompletedTask;
        }

        _thumbnailDocumentId = document.EngineSession.DocumentId.Value;
        _thumbnailCts = new CancellationTokenSource();

        for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            PageThumbnails.Add(new PageThumbnailViewModel(
                pageIndex,
                pageIndex == _tabService.CurrentPageIndex));
        }

        return Task.CompletedTask;
    }

    public async Task EnsureThumbnailLoadedAsync(PageThumbnailViewModel thumbnail)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        var document = _tabService.ActiveDocument;
        var documentId = document?.EngineSession.DocumentId.Value;
        if (document is null ||
            documentId is null ||
            documentId != _thumbnailDocumentId ||
            thumbnail.IsLoading ||
            (uint)thumbnail.PageIndex >= (uint)PageThumbnails.Count ||
            !ReferenceEquals(PageThumbnails[thumbnail.PageIndex], thumbnail))
        {
            return;
        }

        var key = new ThumbnailCacheKey(documentId.Value, thumbnail.PageIndex);
        if (_thumbnailCache.TryGet(key, out var cached) && cached is not null)
        {
            ElliePdfEventSource.Log.CacheHit(
                TelemetryOperation.NextId(),
                checked(Math.Max(1L, (long)Math.Max(1, cached.PixelWidth) * Math.Max(1, cached.PixelHeight) * 4L)));
            thumbnail.Thumbnail = cached;
            return;
        }

        ElliePdfEventSource.Log.CacheMiss(TelemetryOperation.NextId());

        var cancellationToken = _thumbnailCts?.Token ?? CancellationToken.None;
        thumbnail.IsLoading = true;
        try
        {
            var bytes = await RunDocumentRenderAsync(
                document,
                token => _pdfService.RenderPageThumbnailAsync(
                    document,
                    thumbnail.PageIndex,
                    120,
                    160,
                    token),
                cancellationToken);
            var bitmap = await BitmapHelper.CreateBitmapAsync(bytes);

            if (cancellationToken.IsCancellationRequested ||
                !ReferenceEquals(document, _tabService.ActiveDocument) ||
                _thumbnailDocumentId != documentId ||
                (uint)thumbnail.PageIndex >= (uint)PageThumbnails.Count ||
                !ReferenceEquals(PageThumbnails[thumbnail.PageIndex], thumbnail))
            {
                return;
            }

            var decodedBytes = checked(
                Math.Max(1L, (long)Math.Max(1, bitmap.PixelWidth) * Math.Max(1, bitmap.PixelHeight) * 4L));
            if (_thumbnailCache.Set(key, bitmap, decodedBytes))
            {
                thumbnail.Thumbnail = bitmap;
                ElliePdfEventSource.Log.CacheBytes(TelemetryOperation.NextId(), _thumbnailCache.ResidentBytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            thumbnail.IsLoading = false;
        }
    }

    private void NotifyZoomChanged()
    {
        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(EffectiveZoomScale));
    }

    private double ResolveRenderScale()
    {
        var document = _tabService.ActiveDocument;
        if (document is null)
        {
            return 1.0;
        }

        return ZoomScaleCalculator.ResolveScale(
            _tabService.ZoomMode,
            _tabService.ZoomScale,
            _viewportWidth,
            _pageWidthPoints,
            _pageHeightPoints,
            _viewportHeight);
    }

    private async Task RefreshPageDimensionsAsync(CancellationToken cancellationToken)
    {
        var document = _tabService.ActiveDocument;
        if (document is null || document.PageCount == 0)
        {
            _pageWidthPoints = 612f;
            _pageHeightPoints = 792f;
            return;
        }

        var (width, height) = await RunDocumentOtherAsync(
            document,
            token => _pdfService.GetPageSizeAsync(
                document,
                _tabService.CurrentPageIndex,
                token),
            cancellationToken);
        _pageWidthPoints = width;
        _pageHeightPoints = height;
    }

    private void ApplyDefaultZoomMode()
    {
        _tabService.ZoomMode = _settingsService.Settings.DefaultZoomMode;
        if (_tabService.ZoomMode == PdfZoomMode.ActualSize)
        {
            _tabService.ZoomScale = 1.0;
        }

        NotifyZoomChanged();
    }

    private void UpdateSearchHighlights()
    {
        IReadOnlyList<PdfRect> highlights = [];
        var highlightedPageIndex = -1;

        if (_activeSearchMatchIndex < 0 || _activeSearchMatchIndex >= _searchMatches.Count)
        {
            SearchHighlights = highlights;
        }
        else
        {
            var match = _searchMatches[_activeSearchMatchIndex];
            highlightedPageIndex = match.PageIndex;
            highlights = match.HighlightRects;
            SearchHighlights = match.PageIndex == _tabService.CurrentPageIndex
                ? highlights
                : [];
        }

        foreach (var page in ContinuousPages)
        {
            page.SearchHighlights = page.PageIndex == highlightedPageIndex
                ? highlights
                : [];
        }
    }

    public async Task EnsureOutlineLoadedAsync()
    {
        OutlineItems.Clear();
        var document = _tabService.ActiveDocument;
        if (document is null)
        {
            return;
        }

        var outline = await RunDocumentOtherAsync(
            document,
            token => _pdfService.GetOutlineAsync(document, token),
            CancellationToken.None);
        AddOutlineItems(outline, 0);
    }

    private void AddOutlineItems(IReadOnlyList<PdfOutlineItem> items, int depth)
    {
        foreach (var item in items)
        {
            OutlineItems.Add(new OutlineItemViewModel(item, depth));
            AddOutlineItems(item.Children, depth + 1);
        }
    }

    public async Task<ImageSource?> RenderPageImageAsync(
        int pageIndex,
        double scale,
        CancellationToken cancellationToken = default)
    {
        var document = _tabService.ActiveDocument;
        if (document is null || pageIndex < 0 || pageIndex >= document.PageCount)
        {
            return null;
        }

        var rendered = await RunDocumentRenderAsync(
            document,
            token => _pdfService.RenderPageAsync(document, pageIndex, scale, token),
            cancellationToken);
        return BitmapHelper.CreateBitmapFromBgra(rendered.BgraPixels, rendered.Width, rendered.Height);
    }

    public int DocumentPageCount => _tabService.ActiveDocument?.PageCount ?? 0;

    public int CurrentPageIndex => _tabService.CurrentPageIndex;

    private async Task RefreshRenderedPagesAsync(bool navigateToCurrentPage = false)
    {
        var document = _tabService.ActiveDocument;
        if (document is null)
        {
            await RenderCurrentPageAsync();
            return;
        }

        var generation = AdvanceDocumentRenderGeneration(document);
        _singleRenderGeneration = generation;
        _continuousRenderGeneration = generation;
        if (ShowContinuousViewer)
        {
            await RenderContinuousPagesAsync(generation);
            if (navigateToCurrentPage)
            {
                PageNavigationRequested?.Invoke(this, _tabService.CurrentPageIndex);
            }
            return;
        }

        await RenderCurrentPageAsync(generation);
    }

    private Task RenderContinuousPagesAsync(RenderGeneration? generation = null)
    {
        var document = _tabService.ActiveDocument;
        if (document is null || document.PageCount == 0)
        {
            _semanticCts?.Cancel();
            _semanticDocument = null;
            CurrentSemanticPage = null;
            CurrentPermissions = null;
            _continuousRenderCts?.Cancel();
            ContinuousPages.ReplaceAll([]);
            _activeContinuousTileKeys.Clear();
            _continuousDocument = null;
            _continuousScale = 0;
            _continuousPageExtents = null;
            return Task.CompletedTask;
        }

        var scale = ResolveRenderScale();
        PrepareSemanticDocument(document);
        if (ReferenceEquals(_continuousDocument, document)
            && Math.Abs(_continuousScale - scale) < 0.001
            && ContinuousPages.Count == document.PageCount)
        {
            UpdateSearchHighlights();
            return Task.CompletedTask;
        }

        _continuousRenderCts?.Cancel();
        _continuousRenderCts?.Dispose();
        _continuousRenderCts = new CancellationTokenSource();
        _continuousRenderGeneration = generation ?? AdvanceDocumentRenderGeneration(document);

        var placeholderWidth = Math.Max(1, checked((int)Math.Ceiling(_pageWidthPoints * scale)));
        var placeholderHeight = Math.Max(1, checked((int)Math.Ceiling(_pageHeightPoints * scale)));
        var placeholderWidthPoints = Math.Max(1, PageWidthPoints);
        var placeholderHeightPoints = Math.Max(1, PageHeightPoints);
        var currentPageIndex = _tabService.CurrentPageIndex;

        var oldPages = ContinuousPages.ToDictionary(static page => page.PageIndex);
        var oldScale = _continuousScale;
        var pages = Enumerable.Range(0, document.PageCount)
            .Select(pageIndex => new RenderedPageViewModel(
                pageIndex,
                placeholderWidth,
                placeholderHeight,
                placeholderWidthPoints,
                placeholderHeightPoints))
            .ToArray();
        if (oldScale > 0)
        {
            var scaleRatio = scale / oldScale;
            foreach (var page in pages)
            {
                if (!oldPages.TryGetValue(page.PageIndex, out var oldPage) || !oldPage.HasPixels)
                {
                    continue;
                }

                page.ReplaceTiles(oldPage.Tiles.Select(tile => tile with
                {
                    Left = tile.Left * scaleRatio,
                    Top = tile.Top * scaleRatio,
                    Width = tile.Width * scaleRatio,
                    Height = tile.Height * scaleRatio
                }));
                page.SemanticPage = oldPage.SemanticPage;
                page.CanCopy = oldPage.CanCopy;
            }
        }
        ContinuousPages.ReplaceAll(pages);
        _activeContinuousTileKeys.Clear();
        foreach (var page in pages.Where(static page => page.HasPixels))
        {
            _activeContinuousTileKeys[page.PageIndex] = page.Tiles.Select(static tile => tile.Key).ToArray();
        }

        var extents = Enumerable.Range(0, document.PageCount)
            .Select(pageIndex => (double)placeholderHeight + (pageIndex == document.PageCount - 1 ? 0 : 16));
        _continuousPageExtents = new PageExtentIndex(extents);

        _continuousDocument = document;
        _continuousScale = scale;
        UpdateSearchHighlights();
        PageNavigationRequested?.Invoke(this, currentPageIndex);
        return Task.CompletedTask;
    }

    public async Task<bool> EnsureContinuousPageRenderedAsync(
        int pageIndex,
        PageViewport viewport,
        ScrollDirection direction,
        CancellationToken cancellationToken,
        double rasterizationScaleMultiplier = 1)
    {
        if (!double.IsFinite(rasterizationScaleMultiplier) || rasterizationScaleMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rasterizationScaleMultiplier));
        }

        var document = _continuousDocument;
        var generationCancellation = _continuousRenderCts;
        if (document is null
            || generationCancellation is null
            || (uint)pageIndex >= (uint)ContinuousPages.Count)
        {
            return false;
        }

        var page = ContinuousPages[pageIndex];
        var frameOperationId = TelemetryOperation.NextId();
        var frameStarted = TelemetryOperation.StartTimestamp();
        var isCurrentPage = pageIndex == _tabService.CurrentPageIndex;
        if (isCurrentPage && _firstPageRequests.Add(document.EngineSession.DocumentId.Value))
        {
            ElliePdfEventSource.Log.FirstPageRequested(frameOperationId);
        }
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            generationCancellation.Token);
        try
        {
            var rendered = await RunDocumentRenderAsync(
                document,
                token => _pdfService.RenderPageViewportAsync(
                    document,
                    pageIndex,
                    new PageRenderContext(
                        _continuousScale,
                        _rasterizationScale * rasterizationScaleMultiplier,
                        viewport,
                        _continuousRenderGeneration,
                        direction,
                        Mode: _renderMode),
                    token),
                linkedCancellation.Token);
            linkedCancellation.Token.ThrowIfCancellationRequested();

            if (!ReferenceEquals(document, _continuousDocument)
                || !ReferenceEquals(generationCancellation, _continuousRenderCts)
                || (uint)pageIndex >= (uint)ContinuousPages.Count
                || !ReferenceEquals(page, ContinuousPages[pageIndex]))
            {
                return false;
            }

            page.PixelWidth = checked((int)Math.Ceiling(rendered.DisplayWidth));
            page.PixelHeight = checked((int)Math.Ceiling(rendered.DisplayHeight));
            page.PageWidthPoints = checked((float)(rendered.DisplayWidth / _continuousScale));
            page.PageHeightPoints = checked((float)(rendered.DisplayHeight / _continuousScale));
            page.ReplaceTiles(CreateTileViewModels(rendered.Tiles));
            _activeContinuousTileKeys[pageIndex] = page.Tiles.Select(static tile => tile.Key).ToArray();
            page.IsLoading = false;
            RefreshGpuTileProtection();
            QueueSemanticPage(document, pageIndex);

            var frameDuration = TelemetryOperation.ElapsedMicroseconds(frameStarted);
            ElliePdfEventSource.Log.FramePresented(frameOperationId, frameDuration);
            if (isCurrentPage
                && _firstPagePresentations.Add(document.EngineSession.DocumentId.Value))
            {
                BenchmarkFirstPagePresentedMilliseconds =
                    TelemetryOperation.ElapsedMicroseconds(document.OpenStartedTimestamp) / 1000d;
                ElliePdfEventSource.Log.FirstPagePresented(
                    frameOperationId,
                    checked((long)Math.Round(BenchmarkFirstPagePresentedMilliseconds.Value * 1000d)));
            }

            var extent = rendered.DisplayHeight + (pageIndex == ContinuousPages.Count - 1 ? 0 : 16);
            _continuousPageExtents?.UpdateExtent(pageIndex, extent);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
            return false;
        }
        catch (Exception ex) when (ex is PdfResourceLimitException or PdfWorkerUnavailableException)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            if ((uint)pageIndex < (uint)ContinuousPages.Count
                && ReferenceEquals(page, ContinuousPages[pageIndex])
                && !page.HasPixels)
            {
                page.IsLoading = false;
            }
        }
    }

    public void ReleaseContinuousPage(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)ContinuousPages.Count)
        {
            return;
        }

        var page = ContinuousPages[pageIndex];
        page.ClearTiles();
        page.SemanticPage = null;
        _activeContinuousTileKeys.Remove(pageIndex);
        page.IsLoading = true;
        RefreshGpuTileProtection();
    }

    public int GetContinuousPageAtOffset(double verticalOffset, double viewportHeight)
    {
        if (_continuousPageExtents is null)
        {
            return -1;
        }

        var contentOffset = Math.Max(0, verticalOffset - 24);
        return CurrentPageCalculator.Calculate(
            _continuousPageExtents,
            Math.Min(contentOffset, _continuousPageExtents.TotalExtent),
            Math.Max(1, viewportHeight));
    }

    public double GetContinuousPageOffset(int pageIndex)
    {
        if (_continuousPageExtents is null || (uint)pageIndex >= (uint)_continuousPageExtents.Count)
        {
            return 0;
        }

        return 24 + _continuousPageExtents.GetOffset(pageIndex);
    }

    private void ApplyContinuousCurrentPage(int pageIndex)
    {
        UpdateSearchHighlights();
        UpdateSelectedThumbnail(pageIndex);
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(CurrentPageNumberText));
        OnPropertyChanged(nameof(DocumentPageCount));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(CurrentPageNumberText));
        OnPropertyChanged(nameof(DocumentPageCount));
        NotifyZoomChanged();
    }

    public void UpdateSinglePageViewport(PageViewport viewport, ScrollDirection direction = ScrollDirection.None)
    {
        if (!HasDocument || !ShowSinglePageViewer || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        _singleViewport = viewport;
        _singleViewportRenderCts?.Cancel();
        _singleViewportRenderCts?.Dispose();
        _singleViewportRenderCts = new CancellationTokenSource();
        ObserveBackground(
            RenderSingleViewportObservedAsync(direction, _singleViewportRenderCts.Token),
            "reader-single-viewport-render");
    }

    private async Task RenderSingleViewportObservedAsync(
        ScrollDirection direction,
        CancellationToken cancellationToken)
    {
        try
        {
            await RenderSingleViewportAsync(direction, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is PdfResourceLimitException
            or PdfiumDependencyException
            or PdfWorkerUnavailableException
            or InvalidOperationException)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RenderCurrentPageAsync(RenderGeneration? generation = null)
    {
        var document = _tabService.ActiveDocument;
        if (document is null || document.PageCount == 0)
        {
            _continuousRenderCts?.Cancel();
            ContinuousPages.ReplaceAll([]);
            _activeContinuousTileKeys.Clear();
            _continuousDocument = null;
            _continuousScale = 0;
            _continuousPageExtents = null;
            PageImage = null;
            PageTiles.Clear();
            PagePixelWidth = 0;
            PagePixelHeight = 0;
            PageWidthPoints = 0;
            PageHeightPoints = 0;
            OnPropertyChanged(nameof(DisplayScale));
            ToolMode = ReaderToolMode.Read;
            return;
        }

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;
        var pageIndex = _tabService.CurrentPageIndex;

        try
        {
            IsBusy = true;
            await RefreshPageDimensionsAsync(token);
            var scale = ResolveRenderScale();
            _singleRenderGeneration = generation ?? AdvanceDocumentRenderGeneration(document);
            PagePixelWidth = checked((int)Math.Ceiling(_pageWidthPoints * scale));
            PagePixelHeight = checked((int)Math.Ceiling(_pageHeightPoints * scale));
            PageWidthPoints = _pageWidthPoints;
            PageHeightPoints = _pageHeightPoints;
            UpdateSearchHighlights();
            OnPropertyChanged(nameof(DisplayScale));
            UpdateSelectedThumbnail(pageIndex);
            NotifyDocumentChanged();
            NotifyZoomChanged();
            await RenderSingleViewportAsync(ScrollDirection.None, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RenderSingleViewportAsync(ScrollDirection direction, CancellationToken cancellationToken)
    {
        var document = _tabService.ActiveDocument;
        if (document is null || document.PageCount == 0)
        {
            return;
        }

        var frameOperationId = TelemetryOperation.NextId();
        var frameStarted = TelemetryOperation.StartTimestamp();
        var documentIdentity = document.EngineSession.DocumentId.Value;
        var isFirstPageRequest = _firstPageRequests.Add(documentIdentity);
        if (isFirstPageRequest)
        {
            ElliePdfEventSource.Log.FirstPageRequested(frameOperationId);
        }

        var pageIndex = _tabService.CurrentPageIndex;
        var scale = ResolveRenderScale();
        var viewport = new PageViewport(
            Math.Max(0, _singleViewport.X),
            Math.Max(0, _singleViewport.Y),
            Math.Max(1, Math.Min(_singleViewport.Width, Math.Max(1, PagePixelWidth))),
            Math.Max(1, Math.Min(_singleViewport.Height, Math.Max(1, PagePixelHeight))));
        var rendered = await RunDocumentRenderAsync(
            document,
            token => _pdfService.RenderPageViewportAsync(
                document,
                pageIndex,
                new PageRenderContext(
                    scale,
                    _rasterizationScale,
                    viewport,
                    _singleRenderGeneration,
                    direction,
                    InteractionCritical: true,
                    Mode: _renderMode),
                token),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(document, _tabService.ActiveDocument)
            || pageIndex != _tabService.CurrentPageIndex)
        {
            ElliePdfEventSource.Log.RenderRejectedAsStale(frameOperationId);
            return;
        }

        PagePixelWidth = checked((int)Math.Ceiling(rendered.DisplayWidth));
        PagePixelHeight = checked((int)Math.Ceiling(rendered.DisplayHeight));
        PageWidthPoints = checked((float)(rendered.DisplayWidth / scale));
        PageHeightPoints = checked((float)(rendered.DisplayHeight / scale));
        ReplacePageTiles(CreateTileViewModels(rendered.Tiles));
        RefreshGpuTileProtection();
        OnPropertyChanged(nameof(DisplayScale));
        QueueSemanticPage(document, pageIndex);
        var frameDuration = TelemetryOperation.ElapsedMicroseconds(frameStarted);
        ElliePdfEventSource.Log.FramePresented(frameOperationId, frameDuration);
        if (_firstPagePresentations.Add(documentIdentity))
        {
            BenchmarkFirstPagePresentedMilliseconds =
                TelemetryOperation.ElapsedMicroseconds(document.OpenStartedTimestamp) / 1000d;
            ElliePdfEventSource.Log.FirstPagePresented(
                frameOperationId,
                checked((long)Math.Round(BenchmarkFirstPagePresentedMilliseconds.Value * 1000d)));
        }
    }

    private IReadOnlyList<RenderedTileViewModel> CreateTileViewModels(
        IReadOnlyList<RenderedPageTile> tiles)
    {
        var rendered = new RenderedTileViewModel[tiles.Count];
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            if (!_gpuTileCache.TryGet(tile.Key, out var image) || image is null)
            {
                ElliePdfEventSource.Log.CacheMiss(TelemetryOperation.NextId());
                image = BitmapHelper.CreateBitmapFromBgra(
                    tile.BgraPixels,
                    tile.PixelWidth,
                    tile.PixelHeight,
                    tile.Stride);
                _gpuTileCache.Set(tile.Key, image, tile.BgraPixels.LongLength);
                ElliePdfEventSource.Log.CacheBytes(TelemetryOperation.NextId(), _gpuTileCache.ResidentBytes);
            }
            else
            {
                ElliePdfEventSource.Log.CacheHit(TelemetryOperation.NextId(), tile.BgraPixels.LongLength);
            }

            rendered[index] = new RenderedTileViewModel(
                tile.Key,
                image,
                tile.Left,
                tile.Top,
                tile.Width,
                tile.Height,
                tile.IsVisible);
        }

        return rendered;
    }

    private void ReplacePageTiles(IEnumerable<RenderedTileViewModel> tiles)
    {
        PageTiles.Clear();
        foreach (var tile in tiles)
        {
            PageTiles.Add(tile);
        }
    }

    private void RefreshGpuTileProtection()
    {
        _gpuTileCache.ProtectKeys(
            PageTiles.Select(static tile => tile.Key)
                .Concat(_activeContinuousTileKeys.Values.SelectMany(static keys => keys)));
    }

    private void ScaleSinglePagePlaceholder(double oldScale, double newScale)
    {
        if (!ShowSinglePageViewer || oldScale <= 0 || newScale <= 0 || PageTiles.Count == 0)
        {
            return;
        }

        var ratio = newScale / oldScale;
        var scaled = PageTiles.Select(tile => tile with
        {
            Left = tile.Left * ratio,
            Top = tile.Top * ratio,
            Width = tile.Width * ratio,
            Height = tile.Height * ratio
        }).ToArray();
        PagePixelWidth = Math.Max(1, checked((int)Math.Ceiling(PagePixelWidth * ratio)));
        PagePixelHeight = Math.Max(1, checked((int)Math.Ceiling(PagePixelHeight * ratio)));
        ReplacePageTiles(scaled);
        OnPropertyChanged(nameof(DisplayScale));
    }

    private async Task SaveToPathAsync(DocumentTab tab, string outputPath)
    {
        IsBusy = true;

        try
        {
            await _editSaveService.SaveTabAsync(tab, outputPath, CancellationToken.None);
            var committedActiveSource = string.Equals(
                Path.GetFullPath(tab.FilePath),
                Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase);
            if (committedActiveSource)
            {
                _gpuTileCache.Clear();
                _thumbnailCache.Clear();
                _continuousDocument = null;
                _continuousScale = 0;
                _activeContinuousTileKeys.Clear();
                NotifyDocumentChanged();
                SyncTabItems();
                await RefreshRenderedPagesAsync(navigateToCurrentPage: true);
                if (IsThumbnailPanelOpen)
                {
                    await LoadPageThumbnailsAsync();
                    await EnsureThumbnailsLoadedAsync();
                }
            }
            SetStatus(
                AppResources.Format("Reader_StatusSavedEmbedded", Path.GetFileName(outputPath)),
                InfoBarSeverity.Success);
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ConfirmOverwriteAsync(string path)
    {
        if (!_settingsService.Settings.ConfirmOverwriteSave)
        {
            return true;
        }

        var xamlRoot = GetXamlRoot();
        if (xamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = AppResources.Get("Reader_SaveConfirmTitle"),
            Content = AppResources.Format("Reader_SaveConfirmContent", Path.GetFileName(path)),
            PrimaryButtonText = AppResources.Get("Reader_SaveConfirmAction"),
            CloseButtonText = AppResources.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private Microsoft.UI.WindowId GetWindowId() => _uiHost.WindowId;

    private Microsoft.UI.Xaml.XamlRoot? GetXamlRoot() => _uiHost.XamlRoot;

    private void OnTabsChanged(object? sender, EventArgs e) => SyncTabItems();

    private void UpdateSelectedThumbnail(int pageIndex)
    {
        if (!IsThumbnailPanelOpen || PageThumbnails.Count == 0)
        {
            return;
        }

        foreach (var thumbnail in PageThumbnails)
        {
            thumbnail.IsSelected = thumbnail.PageIndex == pageIndex;
        }
    }

    private void SyncTabItems()
    {
        TabItems.Clear();
        foreach (var tab in _tabService.Tabs)
        {
            TabItems.Add(new DocumentTabItemViewModel(tab.Id, tab.DisplayName, tab.IsDirty));
        }

        TabCount = TabItems.Count;
        SelectedTabId = _tabService.ActiveTabId;

        var openDocumentIds = _tabService.Tabs
            .Select(static tab => tab.OpenSession?.EngineSession.DocumentId.Value)
            .OfType<Guid>()
            .ToHashSet();
        foreach (var staleId in _semanticControllers.Keys.Where(id => !openDocumentIds.Contains(id)).ToArray())
        {
            foreach (var key in _semanticCacheKeys.Where(key => key.DocumentId == staleId).ToArray())
            {
                _semanticPageCache.Remove(key);
                _semanticCacheKeys.Remove(key);
            }

            var controller = _semanticControllers[staleId];
            _semanticControllers.Remove(staleId);
            _ = controller.DisposeAsync();
        }
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }

    private void ObserveBackground(Task task, string operationName) =>
        _ = _backgroundTasks.Track(task, operationName);

    private readonly record struct ThumbnailCacheKey(Guid DocumentId, int PageIndex);
    private readonly record struct SemanticCacheKey(Guid DocumentId, int PageIndex);
}
