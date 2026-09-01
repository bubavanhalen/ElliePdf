using ElliePdf.Models;
using ElliePdf.Application;
using ElliePdf.Services;
using ElliePdf.Rendering;
using ElliePdf.Printing;
using ElliePdf.Controls;
using ElliePdf.Domain.Documents;
using ElliePdf.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.ViewManagement;

namespace ElliePdf.Pages;

public sealed partial class ReaderPage : Page
{
    private bool _isSyncingTabs;
    private readonly List<List<Point>> _signatureStrokes = [];
    private List<Point>? _currentSignatureStroke;

    private int? _pendingContinuousPageIndex;
    private ReaderFocusZone _focusZone = ReaderFocusZone.Document;
    private ReaderShellLayout _shellLayout;
    private readonly bool _animationsEnabled;
    private bool _isFocusMode;
    private Control? _focusBeforeTransient;
    private Control? _focusBeforeFocusMode;
    private readonly PageSurfaceBudget _continuousSurfaceBudget = new();
    private readonly Dictionary<UIElement, ContinuousElementWork> _continuousElementWork = [];
    private readonly BackgroundTaskSupervisor _backgroundTasks;
    private readonly UiHostContext _uiHost;
    private bool _readerEventsAttached;
    private readonly CompositeTransform _continuousPinchTransform = new();
    private double _lastContinuousOffset;
    private double _lastSingleViewportY;
    private PageZoomAnchor? _pendingSingleZoomAnchor;
    private ContinuousZoomAnchor? _pendingContinuousZoomAnchor;
    private bool _continuousPinchCenterInitialized;
    private Point _continuousPinchFocalPoint;

    public ReaderPage(
        BackgroundTaskSupervisor backgroundTasks,
        ReaderViewModel viewModel,
        PrintPipeline printPipeline,
        IPdfService printPdfService,
        IDocumentTabService printTabs,
        UiHostContext uiHost)
    {
        _backgroundTasks = backgroundTasks;
        ViewModel = viewModel;
        _printPipeline = printPipeline;
        _printPdfService = printPdfService;
        _printTabs = printTabs;
        _uiHost = uiHost;
        InitializeComponent();
        DataContext = ViewModel;
        ContinuousPagesItems.RenderTransform = _continuousPinchTransform;
        ContinuousPagesItems.ManipulationMode = ManipulationModes.Scale;
        ContinuousPagesItems.ManipulationStarting += ContinuousPagesItems_ManipulationStarting;
        ContinuousPagesItems.ManipulationDelta += ContinuousPagesItems_ManipulationDelta;
        ContinuousPagesItems.ManipulationCompleted += ContinuousPagesItems_ManipulationCompleted;
        InitializePrinting();
        _animationsEnabled = new UISettings().AnimationsEnabled;
        if (!_animationsEnabled)
        {
            Transitions?.Clear();
        }

        PageViewer.ViewportWidthChanged += OnViewportWidthChanged;
        PageViewer.ViewportHeightChanged += OnViewportHeightChanged;
        PageViewer.PagePointerPressed += (_, _) =>
        {
            if (ViewModel.IsReadMode)
            {
                ViewModel.ClosePanels();
            }
        };
        AttachReaderEvents();
        BtnClearSignature.Click += BtnClearSignature_Click;
        SignatureDialog.PrimaryButtonClick += SignatureDialog_PrimaryButtonClick;
        SignatureCanvas.PointerMoved += SignatureCanvas_PointerMoved;
        SignatureCanvas.PointerPressed += SignatureCanvas_PointerPressed;
        SignatureCanvas.PointerReleased += SignatureCanvas_PointerReleased;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ReaderViewModel ViewModel { get; }

    /// <summary>Actual UI realization facts for the in-product benchmark driver.</summary>
    internal BenchmarkReaderSurfaceSnapshot GetBenchmarkSurfaceSnapshot() => new(
        _continuousElementWork.Count,
        _continuousElementWork.Values.Count(static work => work.HasSurface),
        _continuousSurfaceBudget.ActiveCount);

    public FlowDirection UiFlowDirection => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
        ? FlowDirection.RightToLeft
        : FlowDirection.LeftToRight;

    public async Task LoadFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        await ViewModel.LoadFilesAsync(filePaths);
        SyncTabViewItems();
    }

    public void GoToPage(int pageIndex) => ViewModel.GoToPage(pageIndex);

    private void ReaderPage_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    private void ReaderPage_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ObserveBackground(OpenDroppedFilesAsync(e.DataView), "reader-drop-open");
    }

    private async Task OpenDroppedFilesAsync(DataPackageView dataView)
    {
        var items = await dataView.GetStorageItemsAsync();
        var pdfPaths = items
            .OfType<StorageFile>()
            .Where(static file => string.Equals(file.FileType, ".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(static file => file.Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pdfPaths.Length > 0)
        {
            await LoadFilesAsync(pdfPaths);
        }
    }

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AttachReaderEvents();
        ObserveBackground(LoadReaderPageAsync(), "reader-page-load");
    }

    private void AttachReaderEvents()
    {
        if (_readerEventsAttached)
        {
            return;
        }

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.TabItems.CollectionChanged += OnTabItemsChanged;
        ViewModel.ContinuousPages.CollectionChanged += OnContinuousPagesChanged;
        ViewModel.PageNavigationRequested += OnPageNavigationRequested;
        PageViewer.EditSurface.OverlayChanged += EditSurface_OverlayChanged;
        PageViewer.EditSurface.ActiveToolChangeRequested += EditSurface_ActiveToolChangeRequested;
        PageViewer.ViewportChanged += OnSinglePageViewportChanged;
        PageViewer.ZoomRequested += OnSinglePageZoomRequested;
        PageViewer.ZoomFactorRequested += OnSinglePageZoomFactorRequested;
        _readerEventsAttached = true;
    }

    private async Task LoadReaderPageAsync()
    {
        UpdateRasterizationScale();
        if (XamlRoot is not null)
        {
            XamlRoot.Changed += OnXamlRootChanged;
        }
        MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
        ApplyAdaptiveLayout();
        UpdateSidebarHeading();
        ApplyFocusModeChrome();
        SyncTabViewItems();
        await ViewModel.RefreshRecentFilesAsync();
        if (ViewModel.HasDocument)
        {
            await ViewModel.RefreshFromSessionAsync();
            LoadEditSurface();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ResetContinuousElementWork();
        if (_readerEventsAttached)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.TabItems.CollectionChanged -= OnTabItemsChanged;
            ViewModel.ContinuousPages.CollectionChanged -= OnContinuousPagesChanged;
            ViewModel.PageNavigationRequested -= OnPageNavigationRequested;
            PageViewer.EditSurface.OverlayChanged -= EditSurface_OverlayChanged;
            PageViewer.EditSurface.ActiveToolChangeRequested -= EditSurface_ActiveToolChangeRequested;
            PageViewer.ViewportChanged -= OnSinglePageViewportChanged;
            PageViewer.ZoomRequested -= OnSinglePageZoomRequested;
            PageViewer.ZoomFactorRequested -= OnSinglePageZoomFactorRequested;
            _readerEventsAttached = false;
        }
        if (XamlRoot is not null)
        {
            XamlRoot.Changed -= OnXamlRootChanged;
        }
        MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
    }

    private void OnViewportWidthChanged(object? sender, double width) => ViewModel.ViewportWidth = width;

    private void OnViewportHeightChanged(object? sender, double height) => ViewModel.ViewportHeight = height;

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateRasterizationScale();

    private void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        var pressure = MemoryManager.AppMemoryUsageLevel switch
        {
            AppMemoryUsageLevel.OverLimit => RenderMemoryPressureLevel.Critical,
            AppMemoryUsageLevel.High => RenderMemoryPressureLevel.Critical,
            AppMemoryUsageLevel.Medium => RenderMemoryPressureLevel.Moderate,
            _ => RenderMemoryPressureLevel.None
        };
        DispatcherQueue.TryEnqueue(() => ViewModel.ApplyRenderMemoryPressure(pressure));
    }

    private void UpdateRasterizationScale()
    {
        if (XamlRoot is not null)
        {
            ViewModel.RasterizationScale = XamlRoot.RasterizationScale;
        }
        ViewModel.RenderMode = new AccessibilitySettings().HighContrast
            ? RenderMode.HighContrast
            : RenderMode.Normal;
    }

    private void OnSinglePageViewportChanged(object? sender, PageViewport viewport)
    {
        var direction = viewport.Y > _lastSingleViewportY + 0.5
            ? ScrollDirection.Forward
            : viewport.Y < _lastSingleViewportY - 0.5
                ? ScrollDirection.Backward
                : ScrollDirection.None;
        _lastSingleViewportY = viewport.Y;
        ViewModel.UpdateSinglePageViewport(viewport, direction);
    }

    private void OnSinglePageZoomRequested(object? sender, PageZoomRequestEventArgs args)
    {
        _pendingSingleZoomAnchor = PageViewer.CaptureZoomAnchor(args.FocalPoint);
        if (args.ZoomIn)
        {
            ViewModel.ZoomInCommand.Execute(null);
        }
        else
        {
            ViewModel.ZoomOutCommand.Execute(null);
        }
    }

    private void OnSinglePageZoomFactorRequested(object? sender, PageZoomFactorRequestEventArgs args)
    {
        _pendingSingleZoomAnchor = PageViewer.CaptureZoomAnchor(args.FocalPoint);
        ViewModel.ApplyZoomFactor(args.Factor);
    }

    private void ContinuousScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel.ViewportWidth = Math.Max(0, ContinuousScrollViewer.ActualWidth);
        ViewModel.ViewportHeight = Math.Max(0, ContinuousScrollViewer.ActualHeight);
    }

    private void ReaderPage_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyAdaptiveLayout();

    private void ApplyAdaptiveLayout()
    {
        if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
        {
            return;
        }

        _shellLayout = ReaderShellPolicy.Resolve(RootGrid.ActualWidth, RootGrid.ActualHeight);
        ReaderSidebar.Width = Math.Max(1, _shellLayout.SidebarWidth);
        ReaderSidebar.MaxWidth = Math.Max(1, RootGrid.ActualWidth);

        var isSearch = ViewModel.IsSearchPanelOpen;
        var isRightAligned = isSearch != (UiFlowDirection == FlowDirection.RightToLeft);
        ReaderSidebar.HorizontalAlignment = isRightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        ReaderSidebar.BorderThickness = isRightAligned
            ? new Thickness(1, 0, 0, 0)
            : new Thickness(0, 0, 1, 0);

        ReaderSidebar.Margin = _shellLayout.SidebarPresentation == ReaderSidebarPresentation.FullHeightOverlay
            ? new Thickness(0)
            : new Thickness(0, 0, 0, 76);
        Canvas.SetZIndex(
            ReaderSidebar,
            _shellLayout.SidebarPresentation == ReaderSidebarPresentation.FullHeightOverlay ? 40 : 10);

        const double baseDocumentMargin = 56;
        var left = baseDocumentMargin;
        var right = baseDocumentMargin;
        if (ViewModel.IsSidebarOpen && _shellLayout.ReservesDocumentSpace)
        {
            if (isRightAligned)
            {
                right += _shellLayout.SidebarWidth;
            }
            else
            {
                left += _shellLayout.SidebarWidth;
            }
        }

        var documentMargin = new Thickness(left, 0, right, 0);
        PageViewer.Margin = documentMargin;
        ContinuousScrollViewer.Margin = documentMargin;

        // At narrow widths the command bar occupies all available width and relies on dynamic overflow.
        ReaderCommandBar.MaxWidth = RootGrid.ActualWidth < 1_000 ? double.PositiveInfinity : 960;
        EditCommandBar.MaxWidth = ReaderCommandBar.MaxWidth;
    }

    private void UpdateSidebarHeading()
    {
        SidebarHeading.Text = ViewModel.IsSearchPanelOpen
            ? AppResources.Get("Reader_SidebarSearch")
            : ViewModel.IsOutlinePanelOpen
                ? AppResources.Get("Reader_SidebarOutline")
                : AppResources.Get("Reader_SidebarPages");
    }

    private void CloseSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClosePanels();
        RestoreTransientFocus();
    }

    private void SearchCommandButton_Click(object sender, RoutedEventArgs e)
    {
        RememberTransientFocus(sender as Control);
        ViewModel.ToggleSearchPanelCommand.Execute(null);
        if (ViewModel.IsSearchPanelOpen)
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        }
        else
        {
            RestoreTransientFocus();
        }
    }

    private void PagesCommandButton_Click(object sender, RoutedEventArgs e)
    {
        RememberTransientFocus(sender as Control);
        ViewModel.ToggleThumbnailPanelCommand.Execute(null);
        if (ViewModel.IsThumbnailPanelOpen)
        {
            PageThumbnails.Focus(FocusState.Programmatic);
        }
        else
        {
            RestoreTransientFocus();
        }
    }

    private void OutlineCommandButton_Click(object sender, RoutedEventArgs e)
    {
        RememberTransientFocus(sender as Control);
        ViewModel.ToggleOutlinePanelCommand.Execute(null);
        if (ViewModel.IsOutlinePanelOpen)
        {
            OutlineItems.Focus(FocusState.Programmatic);
        }
        else
        {
            RestoreTransientFocus();
        }
    }

    private void RememberTransientFocus(Control? fallback)
    {
        _focusBeforeTransient = FocusManager.GetFocusedElement(XamlRoot) as Control ?? fallback;
    }

    private void RestoreTransientFocus()
    {
        var target = _focusBeforeTransient;
        _focusBeforeTransient = null;
        target?.Focus(FocusState.Programmatic);
    }

    private void ContinuousScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsReadMode)
        {
            ViewModel.ClosePanels();
        }
    }

    private void ContinuousScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint(ContinuousScrollViewer);
        var properties = pointerPoint.Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0)
        {
            return;
        }

        var controlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!controlDown)
        {
            return;
        }

        if (properties.MouseWheelDelta > 0)
        {
            CaptureContinuousZoomAnchor(pointerPoint.Position);
            ViewModel.ZoomInCommand.Execute(null);
        }
        else
        {
            CaptureContinuousZoomAnchor(pointerPoint.Position);
            ViewModel.ZoomOutCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void ContinuousPagesItems_ManipulationStarting(
        object sender,
        ManipulationStartingRoutedEventArgs e)
    {
        e.Mode = ManipulationModes.Scale;
        _continuousPinchCenterInitialized = false;
    }

    private void ContinuousPagesItems_ManipulationDelta(
        object sender,
        ManipulationDeltaRoutedEventArgs e)
    {
        if (!_continuousPinchCenterInitialized)
        {
            _continuousPinchTransform.CenterX = e.Position.X;
            _continuousPinchTransform.CenterY = e.Position.Y;
            _continuousPinchFocalPoint = ContinuousPagesItems
                .TransformToVisual(ContinuousScrollViewer)
                .TransformPoint(e.Position);
            CaptureContinuousZoomAnchor(_continuousPinchFocalPoint);
            _continuousPinchCenterInitialized = true;
        }

        var scale = Math.Clamp(e.Cumulative.Scale, 0.1, 10.0);
        _continuousPinchTransform.ScaleX = scale;
        _continuousPinchTransform.ScaleY = scale;
        e.Handled = true;
    }

    private void ContinuousPagesItems_ManipulationCompleted(
        object sender,
        ManipulationCompletedRoutedEventArgs e)
    {
        var scale = Math.Clamp(e.Cumulative.Scale, 0.1, 10.0);
        _continuousPinchTransform.ScaleX = 1;
        _continuousPinchTransform.ScaleY = 1;
        _continuousPinchCenterInitialized = false;

        if (Math.Abs(scale - 1.0) >= 0.01)
        {
            ViewModel.ApplyZoomFactor(scale);
        }
        else
        {
            _pendingContinuousZoomAnchor = null;
        }

        e.Handled = true;
    }

    private void ContinuousScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!ViewModel.ShowContinuousViewer || ViewModel.ContinuousPages.Count == 0)
        {
            return;
        }

        var bestPageIndex = ViewModel.GetContinuousPageAtOffset(
            ContinuousScrollViewer.VerticalOffset,
            ContinuousScrollViewer.ActualHeight);

        if (bestPageIndex >= 0)
        {
            ViewModel.SetCurrentPageFromContinuousScroll(bestPageIndex);
        }

        var direction = ContinuousScrollViewer.VerticalOffset > _lastContinuousOffset + 0.5
            ? ScrollDirection.Forward
            : ContinuousScrollViewer.VerticalOffset < _lastContinuousOffset - 0.5
                ? ScrollDirection.Backward
                : ScrollDirection.None;
        _lastContinuousOffset = ContinuousScrollViewer.VerticalOffset;
        RefreshContinuousViewportWork(direction);
    }

    private void OnPageNavigationRequested(object? sender, int pageIndex)
    {
        _pendingContinuousPageIndex = pageIndex;
        TryScrollToPendingContinuousPage();
    }

    private void OnContinuousPagesChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            ResetContinuousElementWork(releasePages: false);
            RestoreContinuousZoomAnchor();
        }

        TryScrollToPendingContinuousPage();
    }

    private void ContinuousPagesItems_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        ClearContinuousElementWork(args.Element);
        AutomationProperties.SetPositionInSet(args.Element, args.Index + 1);
        AutomationProperties.SetSizeOfSet(args.Element, ViewModel.ContinuousPages.Count);

        var cancellation = new CancellationTokenSource();
        var work = new ContinuousElementWork(args.Index, cancellation);
        work.Viewport = GetContinuousPageViewport(args.Index);
        work.ViewportKey = ContinuousViewportKey.Create(work.Viewport, ViewModel.RasterizationScale);
        _continuousElementWork[args.Element] = work;
        if (_continuousSurfaceBudget.Request(args.Index))
        {
            StartContinuousElementRender(args.Element, work);
        }
    }

    private void ContinuousPagesItems_ElementClearing(
        ItemsRepeater sender,
        ItemsRepeaterElementClearingEventArgs args)
    {
        ClearContinuousElementWork(args.Element);
    }

    private void StartContinuousElementRender(UIElement element, ContinuousElementWork work)
    {
        if (work.IsStarted || work.Cancellation.IsCancellationRequested)
        {
            return;
        }

        work.IsStarted = true;
        work.Task = _backgroundTasks.Track(
            RenderContinuousElementAsync(element, work),
            $"reader-continuous-page-{work.PageIndex}");
    }

    private async Task RenderContinuousElementAsync(UIElement element, ContinuousElementWork work)
    {
        var succeeded = false;
        var cancellation = work.Cancellation;
        var viewport = work.Viewport;
        var direction = work.Direction;
        try
        {
            succeeded = await ViewModel.EnsureContinuousPageRenderedAsync(
                work.PageIndex,
                viewport,
                direction,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Continuous page render failed: {exception}");
        }

        if (!_continuousElementWork.TryGetValue(element, out var current)
            || !ReferenceEquals(current, work)
            || !ReferenceEquals(work.Cancellation, cancellation))
        {
            return;
        }

        work.HasSurface = succeeded;
        if (!succeeded)
        {
            PromoteContinuousPage(_continuousSurfaceBudget.Release(work.PageIndex));
        }
    }

    private void ClearContinuousElementWork(UIElement element)
    {
        if (!_continuousElementWork.Remove(element, out var work))
        {
            return;
        }

        work.Cancellation.Cancel();
        work.Cancellation.Dispose();
        ViewModel.ReleaseContinuousPage(work.PageIndex);
        PromoteContinuousPage(_continuousSurfaceBudget.Release(work.PageIndex));
    }

    private void RefreshContinuousViewportWork(ScrollDirection direction)
    {
        foreach (var (element, work) in _continuousElementWork.ToArray())
        {
            var viewport = GetContinuousPageViewport(work.PageIndex);
            var key = ContinuousViewportKey.Create(viewport, ViewModel.RasterizationScale);
            if (key == work.ViewportKey && direction == work.Direction)
            {
                continue;
            }

            work.Cancellation.Cancel();
            work.Cancellation.Dispose();
            work.Cancellation = new CancellationTokenSource();
            work.Viewport = viewport;
            work.ViewportKey = key;
            work.Direction = direction;
            work.IsStarted = false;
            StartContinuousElementRender(element, work);
        }
    }

    private PageViewport GetContinuousPageViewport(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)ViewModel.ContinuousPages.Count)
        {
            return new PageViewport(0, 0, 1, 1);
        }

        var page = ViewModel.ContinuousPages[pageIndex];
        var pageTop = ViewModel.GetContinuousPageOffset(pageIndex);
        var pageLeft = Math.Max(0, (ContinuousScrollViewer.ExtentWidth - page.PixelWidth) / 2);
        var x = Math.Clamp(
            ContinuousScrollViewer.HorizontalOffset - pageLeft,
            0,
            Math.Max(0, page.PixelWidth - 1));
        var y = Math.Clamp(
            ContinuousScrollViewer.VerticalOffset - pageTop,
            0,
            Math.Max(0, page.PixelHeight - 1));
        var width = Math.Max(1, Math.Min(page.PixelWidth - x, ContinuousScrollViewer.ViewportWidth));
        var height = Math.Max(1, Math.Min(page.PixelHeight - y, ContinuousScrollViewer.ViewportHeight));
        return new PageViewport(x, y, width, height);
    }

    private void PromoteContinuousPage(int? pageIndex)
    {
        if (pageIndex is not int promotedPage)
        {
            return;
        }

        foreach (var (element, work) in _continuousElementWork)
        {
            if (work.PageIndex == promotedPage)
            {
                StartContinuousElementRender(element, work);
                return;
            }
        }

        PromoteContinuousPage(_continuousSurfaceBudget.Release(promotedPage));
    }

    private void ResetContinuousElementWork(bool releasePages = true)
    {
        foreach (var work in _continuousElementWork.Values)
        {
            work.Cancellation.Cancel();
            work.Cancellation.Dispose();
            if (releasePages)
            {
                ViewModel.ReleaseContinuousPage(work.PageIndex);
            }
        }

        _continuousElementWork.Clear();
        _continuousSurfaceBudget.Clear();
    }

    private void TryScrollToPendingContinuousPage()
    {
        if (_pendingContinuousPageIndex is not int pageIndex
            || pageIndex < 0
            || pageIndex >= ViewModel.ContinuousPages.Count)
        {
            return;
        }

        ContinuousScrollViewer.ChangeView(
            null,
            ViewModel.GetContinuousPageOffset(pageIndex),
            null,
            disableAnimation: true);
        _pendingContinuousPageIndex = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReaderViewModel.PagePixelWidth)
            or nameof(ReaderViewModel.PagePixelHeight))
        {
            if (_pendingSingleZoomAnchor is { } anchor)
            {
                _pendingSingleZoomAnchor = null;
                PageViewer.RestoreZoomAnchor(anchor, disableAnimation: true);
            }
        }

        if (e.PropertyName is nameof(ReaderViewModel.PageImage)
            or nameof(ReaderViewModel.CurrentOverlay)
            or nameof(ReaderViewModel.IsEditMode))
        {
            LoadEditSurface();
        }
        else if (e.PropertyName is nameof(ReaderViewModel.ActiveEditTool)
            or nameof(ReaderViewModel.InkColorHex)
            or nameof(ReaderViewModel.InkThickness))
        {
            ApplyEditSurfaceState();
            UpdateInkPaletteSelection();
            if (!ViewModel.IsInkToolActive)
            {
                InkPalettePopup.IsOpen = false;
            }
        }
        else if (e.PropertyName is nameof(ReaderViewModel.IsSidebarOpen)
            or nameof(ReaderViewModel.IsThumbnailPanelOpen)
            or nameof(ReaderViewModel.IsOutlinePanelOpen)
            or nameof(ReaderViewModel.IsSearchPanelOpen))
        {
            UpdateSidebarHeading();
            ApplyAdaptiveLayout();
        }
        else if (e.PropertyName is nameof(ReaderViewModel.ShowReadToolbar)
            or nameof(ReaderViewModel.ShowEditToolbar)
            or nameof(ReaderViewModel.ShowTabBar))
        {
            ApplyFocusModeChrome();
        }
    }

    private void OnTabItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_isSyncingTabs)
        {
            SyncTabViewItems();
        }
    }

    private void OpenFileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ObserveBackground(PickAndOpenFileAsync(), "reader-open-file");

    private void CloseDocumentButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ObserveBackground(CloseActiveTabAsync(), "reader-close-document");

    private void DocumentTabs_AddTabButtonClick(TabView sender, object args) =>
        ObserveBackground(PickAndOpenFileAsync(), "reader-add-tab");

    private async Task PickAndOpenFileAsync()
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".pdf");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        ViewModel.ClosePanels();
        await ViewModel.LoadDocumentAsync(file.Path);
        SyncTabViewItems();
    }

    private void DocumentTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
        ObserveBackground(CloseRequestedTabAsync(sender, args), "reader-tab-close-request");

    private async Task CloseRequestedTabAsync(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab?.Tag is not Guid tabId)
        {
            return;
        }

        var tabItem = args.Tab;
        if (!await ViewModel.TryCloseTabAsync(tabId))
        {
            return;
        }

        if (sender.TabItems.Contains(tabItem))
        {
            sender.TabItems.Remove(tabItem);
        }

        SyncTabViewItems();
    }

    private void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ObserveBackground(SelectDocumentTabAsync(), "reader-tab-selection");

    private async Task SelectDocumentTabAsync()
    {
        if (_isSyncingTabs || DocumentTabs.SelectedItem is not TabViewItem item || item.Tag is not Guid tabId)
        {
            return;
        }

        ViewModel.ClosePanels();
        await ViewModel.ActivateTabAsync(tabId);
    }

    private void SyncTabViewItems()
    {
        _isSyncingTabs = true;
        try
        {
            DocumentTabs.TabItems.Clear();

            foreach (var tab in ViewModel.TabItems)
            {
                var header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6
                };
                header.Children.Add(new TextBlock
                {
                    Text = tab.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 220
                });
                if (tab.IsDirty)
                {
                    var dirtyMarker = new TextBlock
                    {
                        Text = AppResources.Get("Reader_DirtyMarker"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    AutomationProperties.SetName(dirtyMarker, AppResources.Get("Reader_UnsavedChanges"));
                    header.Children.Add(dirtyMarker);
                }

                var tabViewItem = new TabViewItem
                {
                    Header = header,
                    IsClosable = true,
                    Tag = tab.TabId
                };
                AutomationProperties.SetName(
                    tabViewItem,
                    tab.IsDirty
                        ? AppResources.Format("Reader_TabUnsavedName", tab.Title)
                        : tab.Title);
                DocumentTabs.TabItems.Add(tabViewItem);
            }

            if (ViewModel.SelectedTabId is Guid selectedId)
            {
                var selectedItem = DocumentTabs.TabItems
                    .OfType<TabViewItem>()
                    .FirstOrDefault(item => item.Tag is Guid id && id == selectedId);

                if (selectedItem is not null)
                {
                    DocumentTabs.SelectedItem = selectedItem;
                }
            }
        }
        finally
        {
            _isSyncingTabs = false;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ObserveBackground(ViewModel.SearchCommand.ExecuteAsync(null), "reader-search");
            e.Handled = true;
        }
    }

    private void SearchResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultItemViewModel result)
        {
            ViewModel.GoToSearchResultCommand.Execute(result);
            FocusDocument();
        }
    }

    private void SemanticOverlay_LinkInvoked(object? sender, SemanticLinkInvokedEventArgs e) =>
        ObserveBackground(ViewModel.ActivateSemanticLinkAsync(e.Link), "reader-semantic-link");

    private void SemanticOverlay_FormValueCommitted(object? sender, SemanticFormValueEventArgs e) =>
        ObserveBackground(ViewModel.CommitFormValueAsync(e.Form, e.Value), "reader-semantic-form");

    private void SemanticOverlay_PushButtonInvoked(object? sender, SemanticPushButtonInvokedEventArgs e) =>
        ObserveBackground(ViewModel.InvokePushButtonAsync(e.Form), "reader-semantic-push-button");

    private void PropertiesButton_Click(object sender, RoutedEventArgs e) =>
        ObserveBackground(ShowPropertiesAsync(), "reader-properties");

    private async Task ShowPropertiesAsync()
    {
        var properties = await ViewModel.LoadDocumentPropertiesAsync();
        if (properties is null || XamlRoot is null) return;

        var content = new StackPanel { Spacing = 8 };
        AddProperty(content, "Reader_PropertiesTitle", properties.Title);
        AddProperty(content, "Reader_PropertiesAuthor", properties.Author);
        AddProperty(content, "Reader_PropertiesSubject", properties.Subject);
        AddProperty(content, "Reader_PropertiesCreator", properties.Creator);
        AddProperty(content, "Reader_PropertiesVersion", properties.PdfVersion);
        AddProperty(content, "Reader_PropertiesPages", properties.PageCount);
        AddProperty(content, "Reader_PropertiesPageSize", properties.PageSize);
        AddProperty(content, "Reader_PropertiesSecurity", properties.Security);
        AddProperty(content, "Reader_PropertiesPermissions", properties.Permissions);
        AddProperty(content, "Reader_PropertiesForms", properties.Forms);
        AddProperty(content, "Reader_PropertiesOutline", properties.Outline);
        var dialog = new ContentDialog
        {
            Title = AppResources.Get("Reader_PropertiesDialogTitle"),
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = AppResources.Get("Common_Close"),
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static void AddProperty(StackPanel content, string resourceKey, string value)
    {
        content.Children.Add(new TextBlock
        {
            Text = AppResources.Get(resourceKey),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = value,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void PageNumberBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitPageNumber();
            FocusDocument();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            PageNumberBox.Text = ViewModel.CurrentPageNumberText;
            FocusDocument();
            e.Handled = true;
        }
    }

    private void PageNumberBox_LostFocus(object sender, RoutedEventArgs e) => CommitPageNumber();

    private void CommitPageNumber()
    {
        if (int.TryParse(
                PageNumberBox.Text,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out var pageNumber)
            && pageNumber >= 1
            && pageNumber <= ViewModel.DocumentPageCount)
        {
            ViewModel.GoToPageNumber(pageNumber);
        }

        PageNumberBox.Text = ViewModel.CurrentPageNumberText;
    }

    private void FocusPageNumber()
    {
        PageNumberBox.Focus(FocusState.Programmatic);
        PageNumberBox.SelectAll();
    }

    private void ToggleFocusMode()
    {
        if (!ViewModel.HasDocument)
        {
            return;
        }

        if (!_isFocusMode)
        {
            _focusBeforeFocusMode = FocusManager.GetFocusedElement(XamlRoot) as Control;
            _isFocusMode = true;
            ViewModel.ClosePanels();
            ApplyFocusModeChrome();
            FocusModeAnnouncement.Text = AppResources.Get("Reader_FocusModeOn");
            FocusDocument();
            return;
        }

        _isFocusMode = false;
        ApplyFocusModeChrome();
        FocusModeAnnouncement.Text = AppResources.Get("Reader_FocusModeOff");
        var target = _focusBeforeFocusMode;
        _focusBeforeFocusMode = null;
        target?.Focus(FocusState.Programmatic);
    }

    private void ApplyFocusModeChrome()
    {
        DocumentTabs.Visibility = !_isFocusMode && ViewModel.ShowTabBar
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReaderCommandBar.Visibility = !_isFocusMode && ViewModel.ShowReadToolbar
            ? Visibility.Visible
            : Visibility.Collapsed;
        EditCommandBar.Visibility = !_isFocusMode && ViewModel.ShowEditToolbar
            ? Visibility.Visible
            : Visibility.Collapsed;

        PageViewer.ContentBottomInset = _isFocusMode ? 0 : 112;
    }

    private void CycleFocus(bool reverse)
    {
        _focusZone = ResolveCurrentFocusZone();
        var target = ReaderFocusCycle.Move(_focusZone, reverse, IsFocusZoneAvailable);
        if (FocusZone(target))
        {
            _focusZone = target;
        }
    }

    private ReaderFocusZone ResolveCurrentFocusZone()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (IsDescendantOf(focused, DocumentTabs))
        {
            return ReaderFocusZone.Tabs;
        }

        if (IsDescendantOf(focused, ReaderCommandBar) || IsDescendantOf(focused, EditCommandBar))
        {
            return ReaderFocusZone.Commands;
        }

        if (IsDescendantOf(focused, ReaderSidebar))
        {
            return ReaderFocusZone.Sidebar;
        }

        if (IsDescendantOf(focused, ReaderStatus))
        {
            return ReaderFocusZone.Status;
        }

        return ReaderFocusZone.Document;
    }

    private bool IsFocusZoneAvailable(ReaderFocusZone zone) => zone switch
    {
        ReaderFocusZone.Tabs => DocumentTabs.Visibility == Visibility.Visible,
        ReaderFocusZone.Commands => ReaderCommandBar.Visibility == Visibility.Visible
            || EditCommandBar.Visibility == Visibility.Visible,
        ReaderFocusZone.Sidebar => ViewModel.IsSidebarOpen && ReaderSidebar.Visibility == Visibility.Visible,
        ReaderFocusZone.Document => true,
        ReaderFocusZone.Status => ViewModel.IsStatusOpen,
        _ => false
    };

    private bool FocusZone(ReaderFocusZone zone) => zone switch
    {
        ReaderFocusZone.Tabs => DocumentTabs.Focus(FocusState.Programmatic),
        ReaderFocusZone.Commands => (ViewModel.IsEditMode ? EditCommandBar : ReaderCommandBar)
            .Focus(FocusState.Programmatic),
        ReaderFocusZone.Sidebar => FocusSidebar(),
        ReaderFocusZone.Document => FocusDocument(),
        ReaderFocusZone.Status => FocusFirstWithin(ReaderStatus),
        _ => false
    };

    private bool FocusSidebar()
    {
        if (ViewModel.IsSearchPanelOpen)
        {
            return SearchBox.Focus(FocusState.Programmatic);
        }

        if (ViewModel.IsOutlinePanelOpen)
        {
            return OutlineItems.Focus(FocusState.Programmatic);
        }

        return PageThumbnails.Focus(FocusState.Programmatic);
    }

    private bool FocusDocument()
    {
        if (!ViewModel.HasDocument)
        {
            return EmptyStateOpenButton.Focus(FocusState.Programmatic);
        }

        return ViewModel.ShowContinuousViewer
            ? ContinuousScrollViewer.Focus(FocusState.Programmatic)
            : PageViewer.Focus(FocusState.Programmatic);
    }

    private static bool FocusFirstWithin(DependencyObject root)
    {
        if (FocusManager.FindFirstFocusableElement(root) is Control firstFocusable)
        {
            return firstFocusable.Focus(FocusState.Programmatic);
        }

        return root is Control control && control.Focus(FocusState.Programmatic);
    }

    private static bool IsDescendantOf(DependencyObject? candidate, DependencyObject ancestor)
    {
        while (candidate is not null)
        {
            if (ReferenceEquals(candidate, ancestor))
            {
                return true;
            }

            candidate = VisualTreeHelper.GetParent(candidate);
        }

        return false;
    }

    private void PageThumbnails_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PageThumbnailViewModel thumbnail)
        {
            ViewModel.GoToThumbnailPageCommand.Execute(thumbnail);
        }
    }

    private void OutlineItems_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OutlineItemViewModel item)
        {
            ViewModel.GoToOutlineItemCommand.Execute(item);
        }
    }

    private void RecentFiles_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentFileItemViewModel item)
        {
            ViewModel.ClosePanels();
            ObserveBackground(OpenRecentFileAsync(item), "reader-open-recent");
        }
    }

    private void PageThumbnails_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is PageThumbnailViewModel thumbnail)
        {
            ObserveBackground(
                ViewModel.EnsureThumbnailLoadedAsync(thumbnail),
                $"reader-thumbnail-{thumbnail.PageIndex}");
        }
    }

    private async Task OpenRecentFileAsync(RecentFileItemViewModel item)
    {
        await ViewModel.OpenRecentCommand.ExecuteAsync(item);
        SyncTabViewItems();
    }

    private void LoadEditSurface()
    {
        PageViewer.EditSurface.LoadOverlay(
            ViewModel.CurrentOverlay,
            ViewModel.DisplayScale,
            ViewModel.PagePixelWidth,
            ViewModel.PagePixelHeight);
        ApplyEditSurfaceState();
    }

    private void ApplyEditSurfaceState()
    {
        PageViewer.EditSurface.ActiveTool = ViewModel.ActiveEditTool;
        PageViewer.EditSurface.InkColorHex = ViewModel.InkColorHex;
        PageViewer.EditSurface.InkThickness = ViewModel.InkThickness;
    }

    private void EditSurface_OverlayChanged(object? sender, PageOverlayState overlay) =>
        ViewModel.PersistCurrentOverlay(overlay);

    private void EditSurface_ActiveToolChangeRequested(object? sender, ReaderEditTool tool)
    {
        if (tool == ReaderEditTool.Select)
        {
            ViewModel.UseSelectToolCommand.Execute(null);
            ApplyEditSurfaceState();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        PageViewer.EditSurface.CommitActiveEdits();
        ObserveBackground(ViewModel.SaveCommand.ExecuteAsync(null), "reader-save");
    }

    private void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        PageViewer.EditSurface.CommitActiveEdits();
        ObserveBackground(SaveAsAndReloadEditSurfaceAsync(), "reader-save-as");
    }

    private async Task SaveAsAndReloadEditSurfaceAsync()
    {
        await ViewModel.SaveAsCommand.ExecuteAsync(null);
        LoadEditSurface();
    }

    private void AddTextButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UseTextToolCommand.Execute(null);
        ApplyEditSurfaceState();
    }

    private void AddTextKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = ExecuteShortcut(ReaderShortcut.AddTextAnnotation);
    }

    private void InkToolButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditSurfaceState();
        UpdateInkPaletteSelection();
        OpenInkPalette();
    }

    private void OpenInkPalette()
    {
        InkPalette.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = InkPalette.DesiredSize;
        var anchor = InkToolButton.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
        var left = anchor.X + InkToolButton.ActualWidth / 2 - desired.Width / 2;
        var top = anchor.Y - desired.Height - 10;

        InkPalettePopup.HorizontalOffset = Math.Clamp(left, 8, Math.Max(8, RootGrid.ActualWidth - desired.Width - 8));
        InkPalettePopup.VerticalOffset = Math.Max(8, top);
        InkPalettePopup.IsOpen = true;
    }

    private void InkColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string colorHex })
        {
            ViewModel.SetInkColorCommand.Execute(colorHex);
            ApplyEditSurfaceState();
            UpdateInkPaletteSelection();
        }
    }

    private void InkThicknessButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var thickness))
        {
            ViewModel.SetInkThicknessCommand.Execute(thickness);
            ApplyEditSurfaceState();
            UpdateInkPaletteSelection();
        }
    }

    private void UpdateInkPaletteSelection()
    {
        UpdatePaletteButton(InkBlackButton, ViewModel.InkColorHex == "#000000");
        UpdatePaletteButton(InkRedButton, ViewModel.InkColorHex == "#B3261E");
        UpdatePaletteButton(InkBlueButton, ViewModel.InkColorHex == "#1A73E8");
        UpdatePaletteButton(InkThinButton, Math.Abs(ViewModel.InkThickness - 2) < 0.1);
        UpdatePaletteButton(InkMediumButton, Math.Abs(ViewModel.InkThickness - 5) < 0.1);
        UpdatePaletteButton(InkThickButton, Math.Abs(ViewModel.InkThickness - 9) < 0.1);
    }

    private static void UpdatePaletteButton(Button button, bool isSelected)
    {
        button.BorderThickness = isSelected ? new Thickness(3) : new Thickness(1);
        button.BorderBrush = isSelected
            ? (Brush)Microsoft.UI.Xaml.Application.Current.Resources["EllieSelectedBorderBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Background = isSelected
            ? (Brush)Microsoft.UI.Xaml.Application.Current.Resources["EllieSelectedStateBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetItemStatus(
            button,
            AppResources.Get(isSelected ? "Reader_Selected" : "Reader_NotSelected"));
    }

    private void UndoEditButton_Click(object sender, RoutedEventArgs e) =>
        PageViewer.EditSurface.Undo();

    private void DeleteEditButton_Click(object sender, RoutedEventArgs e) =>
        PageViewer.EditSurface.DeleteSelection();

    private void SignatureButton_Click(object sender, RoutedEventArgs e) =>
        ObserveBackground(ShowSignatureDialogAsync(), "reader-signature-dialog");

    private void SignatureKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = ExecuteShortcut(ReaderShortcut.AddSignatureAnnotation);
    }

    private async Task ShowSignatureDialogAsync()
    {
        _signatureStrokes.Clear();
        _currentSignatureStroke = null;
        SignatureCanvas.Children.Clear();
        SignatureNameBox.Text = string.Empty;
        SignatureDialog.XamlRoot = XamlRoot;
        await SignatureDialog.ShowAsync();
    }

    private void BtnClearSignature_Click(object sender, RoutedEventArgs e)
    {
        _signatureStrokes.Clear();
        _currentSignatureStroke = null;
        SignatureCanvas.Children.Clear();
        SignatureNameBox.Text = string.Empty;
    }

    private void SignatureCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(SignatureCanvas).Position;
        _currentSignatureStroke = [point];
        _signatureStrokes.Add(_currentSignatureStroke);
        SignatureCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SignatureCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_currentSignatureStroke is null)
        {
            return;
        }

        _currentSignatureStroke.Add(e.GetCurrentPoint(SignatureCanvas).Position);
        RedrawSignatureCanvas();
        e.Handled = true;
    }

    private void SignatureCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_currentSignatureStroke is null)
        {
            return;
        }

        SignatureCanvas.ReleasePointerCapture(e.Pointer);
        _currentSignatureStroke = null;
        e.Handled = true;
    }

    private void RedrawSignatureCanvas()
    {
        SignatureCanvas.Children.Clear();
        foreach (var stroke in _signatureStrokes)
        {
            if (stroke.Count < 2)
            {
                continue;
            }

            var points = new PointCollection();
            foreach (var point in stroke)
            {
                points.Add(point);
            }

            SignatureCanvas.Children.Add(new Polyline
            {
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Black),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Points = points
            });
        }
    }

    private void SignatureDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var hasDrawnSignature = _signatureStrokes.Any(stroke => stroke.Count >= 2);
        var typedSignature = SignatureNameBox.Text.Trim();
        if (!hasDrawnSignature && string.IsNullOrWhiteSpace(typedSignature))
        {
            args.Cancel = true;
            return;
        }

        if (!hasDrawnSignature)
        {
            SignatureCanvas.Children.Clear();
            var signatureText = new TextBlock
            {
                Text = typedSignature,
                FontFamily = new FontFamily("Segoe Script"),
                FontSize = 32,
                Width = SignatureCanvas.Width,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetTop(signatureText, 70);
            SignatureCanvas.Children.Add(signatureText);
        }

        ObserveBackground(PlaceSignatureAsync(), "reader-place-signature");
    }

    private async Task PlaceSignatureAsync()
    {
        var renderTarget = new RenderTargetBitmap();
        await renderTarget.RenderAsync(SignatureCanvas);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            stream);
        encoder.SetPixelData(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)renderTarget.PixelWidth,
            (uint)renderTarget.PixelHeight,
            96,
            96,
            (await renderTarget.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
        stream.Seek(0);
        using var memory = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(memory);

        ViewModel.UseSignatureToolCommand.Execute(null);
        ApplyEditSurfaceState();
        PageViewer.EditSurface.PlaceSignature(Convert.ToBase64String(memory.ToArray()));
    }

    private void ReaderPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var controlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var isTextEditing = IsTextEditingElement(e.OriginalSource as DependencyObject);

        if (!controlDown
            && e.Key == VirtualKey.Space
            && IsButtonInvocationElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (ViewModel.IsEditMode)
        {
            if (isTextEditing)
            {
                var textShortcut = ReaderShortcutMap.Resolve(e.Key.ToString(), controlDown, shiftDown, true);
                if (textShortcut == ReaderShortcut.None)
                {
                    return;
                }
            }

            if (e.Key == VirtualKey.Delete)
            {
                PageViewer.EditSurface.DeleteSelection();
                e.Handled = true;
                return;
            }

            if (controlDown && e.Key == VirtualKey.Z)
            {
                PageViewer.EditSurface.Undo();
                e.Handled = true;
                return;
            }
        }

        var shortcut = ReaderShortcutMap.Resolve(e.Key.ToString(), controlDown, shiftDown, isTextEditing);
        if (shortcut == ReaderShortcut.None)
        {
            return;
        }

        e.Handled = ExecuteShortcut(shortcut);
    }

    private bool ExecuteShortcut(ReaderShortcut shortcut)
    {
        switch (shortcut)
        {
            case ReaderShortcut.Open:
                ObserveBackground(PickAndOpenFileAsync(), "reader-shortcut-open");
                return true;
            case ReaderShortcut.CloseTab:
                ObserveBackground(CloseActiveTabAsync(), "reader-shortcut-close-tab");
                return true;
            case ReaderShortcut.Save:
                if (ViewModel.CanSave)
                {
                    PageViewer.EditSurface.CommitActiveEdits();
                    ObserveBackground(ViewModel.SaveCommand.ExecuteAsync(null), "reader-shortcut-save");
                }

                return true;
            case ReaderShortcut.SaveAs:
                if (ViewModel.HasDocument)
                {
                    PageViewer.EditSurface.CommitActiveEdits();
                    ObserveBackground(ViewModel.SaveAsCommand.ExecuteAsync(null), "reader-shortcut-save-as");
                }

                return true;
            case ReaderShortcut.NextTab:
                CycleTab(reverse: false);
                return true;
            case ReaderShortcut.PreviousTab:
                CycleTab(reverse: true);
                return true;
            case ReaderShortcut.GoToPage:
                FocusPageNumber();
                return true;
            case ReaderShortcut.Find:
                RememberTransientFocus(SearchCommandButton);
                if (!ViewModel.IsSearchPanelOpen)
                {
                    ViewModel.ToggleSearchPanelCommand.Execute(null);
                }

                if (ViewModel.IsSearchPanelOpen)
                {
                    SearchBox.Focus(FocusState.Programmatic);
                    SearchBox.SelectAll();
                }

                return true;
            case ReaderShortcut.NextSearchResult:
                ObserveBackground(ViewModel.NextSearchMatchCommand.ExecuteAsync(null), "reader-next-search-result");
                return true;
            case ReaderShortcut.PreviousSearchResult:
                ObserveBackground(ViewModel.PreviousSearchMatchCommand.ExecuteAsync(null), "reader-previous-search-result");
                return true;
            case ReaderShortcut.Print:
                PrintButton_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs());
                return true;
            case ReaderShortcut.FirstPage:
                ViewModel.GoToPage(0);
                return true;
            case ReaderShortcut.LastPage:
                ViewModel.GoToPage(Math.Max(0, ViewModel.DocumentPageCount - 1));
                return true;
            case ReaderShortcut.ScrollHome:
                ScrollToEdge(end: false);
                return true;
            case ReaderShortcut.ScrollEnd:
                ScrollToEdge(end: true);
                return true;
            case ReaderShortcut.ViewportBackward:
                ScrollByViewport(forward: false);
                return true;
            case ReaderShortcut.ViewportForward:
                ScrollByViewport(forward: true);
                return true;
            case ReaderShortcut.CopySelection:
                // The semantic text layer owns selection/copy. Do not manufacture clipboard text here.
                return false;
            case ReaderShortcut.ZoomIn:
                ViewModel.ZoomInCommand.Execute(null);
                return true;
            case ReaderShortcut.ZoomOut:
                ViewModel.ZoomOutCommand.Execute(null);
                return true;
            case ReaderShortcut.AddTextAnnotation:
                if (!ViewModel.IsEditMode || !ViewModel.IsLabsEnabled)
                {
                    return false;
                }

                AddTextButton_Click(this, new RoutedEventArgs());
                return true;
            case ReaderShortcut.AddSignatureAnnotation:
                if (!ViewModel.IsEditMode || !ViewModel.IsLabsEnabled)
                {
                    return false;
                }

                SignatureButton_Click(this, new RoutedEventArgs());
                return true;
            case ReaderShortcut.CycleFocusForward:
                CycleFocus(reverse: false);
                return true;
            case ReaderShortcut.CycleFocusBackward:
                CycleFocus(reverse: true);
                return true;
            case ReaderShortcut.ToggleFocusMode:
                ToggleFocusMode();
                return true;
            case ReaderShortcut.DismissTransient:
                return DismissTopmostTransient();
            default:
                return false;
        }
    }

    private bool DismissTopmostTransient()
    {
        if (InkPalettePopup.IsOpen)
        {
            InkPalettePopup.IsOpen = false;
            InkToolButton.Focus(FocusState.Programmatic);
            return true;
        }

        if (ReaderCommandBar.IsOpen)
        {
            ReaderCommandBar.IsOpen = false;
            ReaderCommandBar.Focus(FocusState.Programmatic);
            return true;
        }

        if (EditCommandBar.IsOpen)
        {
            EditCommandBar.IsOpen = false;
            EditCommandBar.Focus(FocusState.Programmatic);
            return true;
        }

        if (ViewModel.IsSidebarOpen)
        {
            ViewModel.ClosePanels();
            RestoreTransientFocus();
            return true;
        }

        if (ViewModel.IsStatusOpen)
        {
            ViewModel.DismissStatus();
            FocusDocument();
            return true;
        }

        if (ViewModel.IsEditMode)
        {
            ViewModel.UseSelectToolCommand.Execute(null);
            PageViewer.EditSurface.ClearSelection();
            ApplyEditSurfaceState();
            return true;
        }

        return false;
    }

    private void CycleTab(bool reverse)
    {
        if (DocumentTabs.TabItems.Count < 2)
        {
            return;
        }

        var selectedIndex = Math.Max(0, DocumentTabs.SelectedIndex);
        var direction = reverse ? -1 : 1;
        DocumentTabs.SelectedIndex =
            (selectedIndex + direction + DocumentTabs.TabItems.Count) % DocumentTabs.TabItems.Count;
    }

    private void ScrollByViewport(bool forward)
    {
        if (ViewModel.ShowContinuousViewer)
        {
            var distance = Math.Max(44, ContinuousScrollViewer.ViewportHeight * 0.9);
            var target = Math.Clamp(
                ContinuousScrollViewer.VerticalOffset + (forward ? distance : -distance),
                0,
                ContinuousScrollViewer.ScrollableHeight);
            ContinuousScrollViewer.ChangeView(null, target, null, disableAnimation: !_animationsEnabled);
            return;
        }

        PageViewer.ScrollByViewport(forward, disableAnimation: !_animationsEnabled);
    }

    private void ScrollToEdge(bool end)
    {
        if (ViewModel.ShowContinuousViewer)
        {
            ContinuousScrollViewer.ChangeView(
                null,
                end ? ContinuousScrollViewer.ScrollableHeight : 0,
                null,
                disableAnimation: !_animationsEnabled);
            return;
        }

        PageViewer.ScrollToEdge(end, disableAnimation: !_animationsEnabled);
    }

    private static bool IsTextEditingElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBox or RichEditBox or NumberBox or PasswordBox)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private static bool IsButtonInvocationElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void ObserveBackground(Task task, string operationName) =>
        _ = _backgroundTasks.Track(task, operationName);

    private void CaptureContinuousZoomAnchor(Point focalPoint)
    {
        var pageIndex = Math.Clamp(
            ViewModel.CurrentPageIndex,
            0,
            Math.Max(0, ViewModel.ContinuousPages.Count - 1));
        if ((uint)pageIndex >= (uint)ViewModel.ContinuousPages.Count)
        {
            return;
        }

        var page = ViewModel.ContinuousPages[pageIndex];
        var pageTop = ViewModel.GetContinuousPageOffset(pageIndex);
        var pageLeft = Math.Max(0, (ContinuousScrollViewer.ExtentWidth - page.PixelWidth) / 2);
        _pendingContinuousZoomAnchor = new ContinuousZoomAnchor(
            pageIndex,
            Math.Clamp((ContinuousScrollViewer.HorizontalOffset + focalPoint.X - pageLeft) / Math.Max(1, page.PixelWidth), 0, 1),
            Math.Clamp((ContinuousScrollViewer.VerticalOffset + focalPoint.Y - pageTop) / Math.Max(1, page.PixelHeight), 0, 1),
            focalPoint);
    }

    private void RestoreContinuousZoomAnchor()
    {
        if (_pendingContinuousZoomAnchor is not { } anchor)
        {
            return;
        }

        _pendingContinuousZoomAnchor = null;
        DispatcherQueue.TryEnqueue(() =>
        {
            if ((uint)anchor.PageIndex >= (uint)ViewModel.ContinuousPages.Count)
            {
                return;
            }

            var page = ViewModel.ContinuousPages[anchor.PageIndex];
            var pageTop = ViewModel.GetContinuousPageOffset(anchor.PageIndex);
            var pageLeft = Math.Max(0, (ContinuousScrollViewer.ExtentWidth - page.PixelWidth) / 2);
            var horizontal = pageLeft + anchor.NormalizedX * page.PixelWidth - anchor.FocalPoint.X;
            var vertical = pageTop + anchor.NormalizedY * page.PixelHeight - anchor.FocalPoint.Y;
            ContinuousScrollViewer.ChangeView(
                Math.Clamp(horizontal, 0, ContinuousScrollViewer.ScrollableWidth),
                Math.Clamp(vertical, 0, ContinuousScrollViewer.ScrollableHeight),
                null,
                disableAnimation: true);
        });
    }

    private async Task CloseActiveTabAsync()
    {
        if (ViewModel.SelectedTabId is not Guid tabId)
        {
            return;
        }

        if (!await ViewModel.TryCloseTabAsync(tabId))
        {
            return;
        }

        SyncTabViewItems();
    }

    private sealed class ContinuousElementWork(int pageIndex, CancellationTokenSource cancellation)
    {
        public int PageIndex { get; } = pageIndex;
        public CancellationTokenSource Cancellation { get; set; } = cancellation;
        public Task? Task { get; set; }
        public bool IsStarted { get; set; }
        public bool HasSurface { get; set; }
        public PageViewport Viewport { get; set; } = new(0, 0, 1, 1);
        public ContinuousViewportKey ViewportKey { get; set; }
        public ScrollDirection Direction { get; set; }
    }

    private readonly record struct ContinuousViewportKey(int X, int Y, int Width, int Height, int Dpi64)
    {
        public static ContinuousViewportKey Create(PageViewport viewport, double rasterizationScale) => new(
            checked((int)Math.Floor(viewport.X / 256)),
            checked((int)Math.Floor(viewport.Y / 256)),
            checked((int)Math.Ceiling(viewport.Width / 256)),
            checked((int)Math.Ceiling(viewport.Height / 256)),
            checked((int)Math.Ceiling(rasterizationScale * 64)));
    }

    private readonly record struct ContinuousZoomAnchor(
        int PageIndex,
        double NormalizedX,
        double NormalizedY,
        Point FocalPoint);
}

internal readonly record struct BenchmarkReaderSurfaceSnapshot(
    int RealizedControls,
    int PageSubscriptions,
    int ActiveSurfaceCount);
