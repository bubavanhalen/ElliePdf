using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ElliePdf.ViewModels;
using Windows.Foundation;
using ElliePdf.Semantics;

namespace ElliePdf.Controls;

public sealed partial class PdfPageViewer : UserControl
{
    private readonly CompositeTransform _pinchTransform = new();
    private bool _pinchCenterInitialized;
    public static readonly DependencyProperty PageTilesProperty =
        DependencyProperty.Register(
            nameof(PageTiles),
            typeof(object),
            typeof(PdfPageViewer),
            new PropertyMetadata(null, OnPageTilesChanged));

    public static readonly DependencyProperty PageDisplayWidthProperty =
        DependencyProperty.Register(
            nameof(PageDisplayWidth),
            typeof(double),
            typeof(PdfPageViewer),
            new PropertyMetadata(1d, OnPageGeometryChanged));

    public static readonly DependencyProperty PageDisplayHeightProperty =
        DependencyProperty.Register(
            nameof(PageDisplayHeight),
            typeof(double),
            typeof(PdfPageViewer),
            new PropertyMetadata(1d, OnPageGeometryChanged));

    public static readonly DependencyProperty IsChromelessProperty =
        DependencyProperty.Register(
            nameof(IsChromeless),
            typeof(bool),
            typeof(PdfPageViewer),
            new PropertyMetadata(false, OnChromelessChanged));

    public static readonly DependencyProperty ContentBottomInsetProperty =
        DependencyProperty.Register(
            nameof(ContentBottomInset),
            typeof(double),
            typeof(PdfPageViewer),
            new PropertyMetadata(0.0, OnContentBottomInsetChanged));

    public static readonly DependencyProperty IsOverlayEnabledProperty =
        DependencyProperty.Register(
            nameof(IsOverlayEnabled),
            typeof(bool),
            typeof(PdfPageViewer),
            new PropertyMetadata(false, OnOverlayEnabledChanged));

    public static readonly DependencyProperty SearchHighlightsProperty =
        DependencyProperty.Register(
            nameof(SearchHighlights),
            typeof(IReadOnlyList<PdfRect>),
            typeof(PdfPageViewer),
            new PropertyMetadata(null, OnSearchHighlightsChanged));

    public static readonly DependencyProperty PageHeightPointsProperty =
        DependencyProperty.Register(
            nameof(PageHeightPoints),
            typeof(float),
            typeof(PdfPageViewer),
            new PropertyMetadata(0f, OnSearchHighlightsChanged));

    public static readonly DependencyProperty DisplayScaleProperty =
        DependencyProperty.Register(
            nameof(DisplayScale),
            typeof(double),
            typeof(PdfPageViewer),
            new PropertyMetadata(1.0, OnSearchHighlightsChanged));

    public static readonly DependencyProperty SemanticPageProperty = DependencyProperty.Register(
        nameof(SemanticPage), typeof(object), typeof(PdfPageViewer),
        new PropertyMetadata(null, OnSemanticProjectionChanged));

    public static readonly DependencyProperty CanCopyProperty = DependencyProperty.Register(
        nameof(CanCopy), typeof(bool), typeof(PdfPageViewer),
        new PropertyMetadata(false, OnSemanticProjectionChanged));

    public PdfPageViewer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        PageScrollViewer.PointerWheelChanged += OnPointerWheelChanged;
        PageScrollViewer.ViewChanged += OnViewChanged;
        PageHost.RenderTransform = _pinchTransform;
        PageHost.ManipulationMode = ManipulationModes.Scale;
        PageHost.ManipulationStarting += OnPinchStarting;
        PageHost.ManipulationDelta += OnPinchDelta;
        PageHost.ManipulationCompleted += OnPinchCompleted;
        TileSurface.SizeChanged += (_, _) => UpdateSearchHighlights();
        SemanticOverlay.LinkInvoked += (_, args) => LinkInvoked?.Invoke(this, args);
        SemanticOverlay.FormValueCommitted += (_, args) => FormValueCommitted?.Invoke(this, args);
        SemanticOverlay.PushButtonInvoked += (_, args) => PushButtonInvoked?.Invoke(this, args);
        ApplyChromeless();
    }

    public IEnumerable<RenderedTileViewModel>? PageTiles
    {
        get => GetValue(PageTilesProperty) as IEnumerable<RenderedTileViewModel>;
        set => SetValue(PageTilesProperty, value);
    }

    public double PageDisplayWidth
    {
        get => (double)GetValue(PageDisplayWidthProperty);
        set => SetValue(PageDisplayWidthProperty, value);
    }

    public double PageDisplayHeight
    {
        get => (double)GetValue(PageDisplayHeightProperty);
        set => SetValue(PageDisplayHeightProperty, value);
    }

    public bool IsChromeless
    {
        get => (bool)GetValue(IsChromelessProperty);
        set => SetValue(IsChromelessProperty, value);
    }

    public double ContentBottomInset
    {
        get => (double)GetValue(ContentBottomInsetProperty);
        set => SetValue(ContentBottomInsetProperty, value);
    }

    public bool IsOverlayEnabled
    {
        get => (bool)GetValue(IsOverlayEnabledProperty);
        set => SetValue(IsOverlayEnabledProperty, value);
    }

    public IReadOnlyList<PdfRect>? SearchHighlights
    {
        get => (IReadOnlyList<PdfRect>?)GetValue(SearchHighlightsProperty);
        set => SetValue(SearchHighlightsProperty, value);
    }

    public float PageHeightPoints
    {
        get => (float)GetValue(PageHeightPointsProperty);
        set => SetValue(PageHeightPointsProperty, value);
    }

    public double DisplayScale
    {
        get => (double)GetValue(DisplayScaleProperty);
        set => SetValue(DisplayScaleProperty, value);
    }

    public SemanticPageSnapshot? SemanticPage
    {
        get => GetValue(SemanticPageProperty) as SemanticPageSnapshot;
        set => SetValue(SemanticPageProperty, value);
    }

    public bool CanCopy
    {
        get => (bool)GetValue(CanCopyProperty);
        set => SetValue(CanCopyProperty, value);
    }

    public PdfEditSurface EditSurface => PageEditSurface;

    public event EventHandler<double>? ViewportWidthChanged;

    public event EventHandler<double>? ViewportHeightChanged;

    public event EventHandler? ZoomInRequested;

    public event EventHandler? ZoomOutRequested;

    public event EventHandler<PageZoomRequestEventArgs>? ZoomRequested;

    public event EventHandler<PageZoomFactorRequestEventArgs>? ZoomFactorRequested;

    public event EventHandler? PagePointerPressed;

    public event EventHandler<PageViewport>? ViewportChanged;

    public event EventHandler<SemanticLinkInvokedEventArgs>? LinkInvoked;

    public event EventHandler<SemanticFormValueEventArgs>? FormValueCommitted;

    public event EventHandler<SemanticPushButtonInvokedEventArgs>? PushButtonInvoked;

    public bool ScrollByViewport(bool forward, bool disableAnimation)
    {
        if (PageScrollViewer.ScrollableHeight <= 0)
        {
            return false;
        }

        var distance = Math.Max(44, PageScrollViewer.ViewportHeight * 0.9);
        var target = Math.Clamp(
            PageScrollViewer.VerticalOffset + (forward ? distance : -distance),
            0,
            PageScrollViewer.ScrollableHeight);
        PageScrollViewer.ChangeView(null, target, null, disableAnimation);
        return true;
    }

    public void ScrollToEdge(bool end, bool disableAnimation) =>
        PageScrollViewer.ChangeView(
            null,
            end ? PageScrollViewer.ScrollableHeight : 0,
            null,
            disableAnimation);

    public PageZoomAnchor CaptureZoomAnchor(Point focalPoint)
    {
        var origin = PageHost.TransformToVisual(PageScrollViewer).TransformPoint(new Point());
        return new PageZoomAnchor(
            PageDisplayWidth <= 0 ? 0.5 : Math.Clamp((focalPoint.X - origin.X) / PageDisplayWidth, 0, 1),
            PageDisplayHeight <= 0 ? 0.5 : Math.Clamp((focalPoint.Y - origin.Y) / PageDisplayHeight, 0, 1),
            focalPoint);
    }

    public void RestoreZoomAnchor(PageZoomAnchor anchor, bool disableAnimation = true)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var origin = PageHost.TransformToVisual(PageScrollViewer).TransformPoint(new Point());
            var pageX = anchor.NormalizedX * PageDisplayWidth;
            var pageY = anchor.NormalizedY * PageDisplayHeight;
            var deltaX = origin.X + pageX - anchor.FocalPoint.X;
            var deltaY = origin.Y + pageY - anchor.FocalPoint.Y;
            PageScrollViewer.ChangeView(
                Math.Clamp(PageScrollViewer.HorizontalOffset + deltaX, 0, PageScrollViewer.ScrollableWidth),
                Math.Clamp(PageScrollViewer.VerticalOffset + deltaY, 0, PageScrollViewer.ScrollableHeight),
                null,
                disableAnimation);
            ReportViewportSize();
        });
    }

    private static void OnPageTilesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageViewer viewer)
        {
            viewer.TileSurface.Tiles = e.NewValue as IEnumerable<RenderedTileViewModel>;
        }
    }

    private static void OnPageGeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageViewer viewer)
        {
            var width = Math.Max(1, viewer.PageDisplayWidth);
            var height = Math.Max(1, viewer.PageDisplayHeight);
            viewer.PageHost.Width = width;
            viewer.PageHost.Height = height;
            viewer.TileSurface.Width = width;
            viewer.TileSurface.Height = height;
            viewer.DispatcherQueue.TryEnqueue(viewer.ReportViewportSize);
        }
    }

    private static void OnChromelessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageViewer viewer)
        {
            viewer.ApplyChromeless();
            viewer.ReportViewportSize();
        }
    }

    private static void OnContentBottomInsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageViewer viewer)
        {
            viewer.ApplyContentInset();
        }
    }

    private static void OnOverlayEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageViewer viewer)
        {
            viewer.ApplyOverlayEnabled();
        }
    }

    private static void OnSearchHighlightsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageViewer viewer)
        {
            viewer.UpdateSearchHighlights();
            viewer.SemanticOverlay.PageHeightPoints = viewer.PageHeightPoints;
            viewer.SemanticOverlay.DisplayScale = viewer.DisplayScale;
        }
    }

    private static void OnSemanticProjectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PdfPageViewer viewer) return;
        viewer.SemanticOverlay.SemanticPage = viewer.SemanticPage;
        viewer.SemanticOverlay.CanCopy = viewer.CanCopy;
        viewer.SemanticOverlay.PageHeightPoints = viewer.PageHeightPoints;
        viewer.SemanticOverlay.DisplayScale = viewer.DisplayScale;
    }

    private void ApplyChromeless()
    {
        if (IsChromeless)
        {
            PageChrome.Padding = new Thickness(0);
            PageChrome.Background = null;
            PageChrome.CornerRadius = new CornerRadius(0);
            PageScrollViewer.Background = null;
            ApplyContentInset();
            return;
        }

        PageChrome.Padding = new Thickness(24);
        PageChrome.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        PageChrome.CornerRadius = new CornerRadius(4);
        PageScrollViewer.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["LayerFillColorDefaultBrush"];
        ApplyContentInset();
    }

    private void ApplyContentInset() =>
        PageChrome.Margin = new Thickness(0, 0, 0, Math.Max(0, ContentBottomInset));

    private void ApplyOverlayEnabled()
    {
        // The overlay always draws: annotations are held out of the page while a document is open,
        // so hiding it would make them disappear whenever the user is not editing. Only pointer
        // interaction is gated on edit mode.
        PageEditSurface.IsHitTestVisible = IsOverlayEnabled;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PageScrollViewer.PointerPressed += OnPagePointerPressed;
        ReportViewportSize();
    }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e) =>
        PagePointerPressed?.Invoke(this, EventArgs.Empty);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ReportViewportSize();

    private void ReportViewportSize()
    {
        var chromePadding = IsChromeless ? 0 : 48;
        var width = Math.Max(0, PageScrollViewer.ActualWidth - chromePadding);
        var height = Math.Max(0, PageScrollViewer.ActualHeight - chromePadding);
        ViewportWidthChanged?.Invoke(this, width);
        ViewportHeightChanged?.Invoke(this, height);
        var viewport = GetPageViewport();
        if (viewport.Width > 0 && viewport.Height > 0)
        {
            ViewportChanged?.Invoke(this, viewport);
        }
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => ReportViewportSize();

    private PageViewport GetPageViewport()
    {
        if (PageDisplayWidth <= 0 || PageDisplayHeight <= 0
            || PageScrollViewer.ViewportWidth <= 0 || PageScrollViewer.ViewportHeight <= 0)
        {
            return new PageViewport(0, 0, 1, 1);
        }

        var origin = PageHost.TransformToVisual(PageScrollViewer).TransformPoint(new Point());
        var x = Math.Clamp(-origin.X, 0, PageDisplayWidth);
        var y = Math.Clamp(-origin.Y, 0, PageDisplayHeight);
        var width = Math.Max(1, Math.Min(PageDisplayWidth - x, PageScrollViewer.ViewportWidth));
        var height = Math.Max(1, Math.Min(PageDisplayHeight - y, PageScrollViewer.ViewportHeight));
        return new PageViewport(x, y, width, height);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(PageScrollViewer).Properties;
        if (!properties.IsHorizontalMouseWheel && properties.MouseWheelDelta != 0)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                var focalPoint = e.GetCurrentPoint(PageScrollViewer).Position;
                if (ZoomRequested is not null)
                {
                    ZoomRequested.Invoke(
                        this,
                        new PageZoomRequestEventArgs(properties.MouseWheelDelta > 0, focalPoint));
                }
                else
                {
                    if (properties.MouseWheelDelta > 0)
                    {
                        ZoomInRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        ZoomOutRequested?.Invoke(this, EventArgs.Empty);
                    }
                }

                e.Handled = true;
            }
        }
    }

    private void OnPinchStarting(object sender, ManipulationStartingRoutedEventArgs e)
    {
        e.Mode = ManipulationModes.Scale;
        _pinchCenterInitialized = false;
    }

    private void OnPinchDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_pinchCenterInitialized)
        {
            _pinchTransform.CenterX = e.Position.X;
            _pinchTransform.CenterY = e.Position.Y;
            _pinchCenterInitialized = true;
        }

        var scale = Math.Clamp(e.Cumulative.Scale, 0.1, 10.0);
        _pinchTransform.ScaleX = scale;
        _pinchTransform.ScaleY = scale;
        e.Handled = true;
    }

    private void OnPinchCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        var scale = Math.Clamp(e.Cumulative.Scale, 0.1, 10.0);
        var focalPoint = PageHost.TransformToVisual(PageScrollViewer).TransformPoint(e.Position);
        if (Math.Abs(scale - 1.0) >= 0.01)
        {
            ZoomFactorRequested?.Invoke(
                this,
                new PageZoomFactorRequestEventArgs(scale, focalPoint));
        }

        _pinchTransform.ScaleX = 1;
        _pinchTransform.ScaleY = 1;
        _pinchCenterInitialized = false;
        e.Handled = true;
    }

    private void UpdateSearchHighlights()
    {
        SearchHighlightCanvas.Children.Clear();

        if (SearchHighlights is null || SearchHighlights.Count == 0 || PageHeightPoints <= 0 || DisplayScale <= 0)
        {
            return;
        }

        var highlightBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 214, 102));

        foreach (var rect in SearchHighlights)
        {
            var left = rect.Left * DisplayScale;
            var top = (PageHeightPoints - rect.Bottom) * DisplayScale;
            var width = Math.Max(1, (rect.Right - rect.Left) * DisplayScale);
            var height = Math.Max(1, (rect.Bottom - rect.Top) * DisplayScale);

            SearchHighlightCanvas.Children.Add(new Rectangle
            {
                Fill = highlightBrush,
                Width = width,
                Height = height,
                RadiusX = 2,
                RadiusY = 2
            });

            Canvas.SetLeft(SearchHighlightCanvas.Children[^1], left);
            Canvas.SetTop(SearchHighlightCanvas.Children[^1], top);
        }
    }
}

public readonly record struct PageZoomAnchor(double NormalizedX, double NormalizedY, Point FocalPoint);

public sealed class PageZoomRequestEventArgs(bool zoomIn, Point focalPoint) : EventArgs
{
    public bool ZoomIn { get; } = zoomIn;
    public Point FocalPoint { get; } = focalPoint;
}

public sealed class PageZoomFactorRequestEventArgs(double factor, Point focalPoint) : EventArgs
{
    public double Factor { get; } = factor;
    public Point FocalPoint { get; } = focalPoint;
}
