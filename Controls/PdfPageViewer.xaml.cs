using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace ElliePdf.Controls;

public sealed partial class PdfPageViewer : UserControl
{
    public static readonly DependencyProperty PageSourceProperty =
        DependencyProperty.Register(
            nameof(PageSource),
            typeof(BitmapImage),
            typeof(PdfPageViewer),
            new PropertyMetadata(null, OnPageSourceChanged));

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

    public PdfPageViewer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        PageScrollViewer.PointerWheelChanged += OnPointerWheelChanged;
        PageImage.SizeChanged += (_, _) => UpdateSearchHighlights();
        ApplyChromeless();
    }

    public BitmapImage? PageSource
    {
        get => (BitmapImage?)GetValue(PageSourceProperty);
        set => SetValue(PageSourceProperty, value);
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

    public PdfEditSurface EditSurface => PageEditSurface;

    public event EventHandler<double>? ViewportWidthChanged;

    public event EventHandler<double>? ViewportHeightChanged;

    public event EventHandler? ZoomInRequested;

    public event EventHandler? ZoomOutRequested;

    public event EventHandler? PagePointerPressed;

    private static void OnPageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageViewer viewer)
        {
            viewer.PageImage.Source = e.NewValue as BitmapImage;
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
        }
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
        PageChrome.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        PageChrome.CornerRadius = new CornerRadius(4);
        PageScrollViewer.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
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
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(PageScrollViewer).Properties;
        if (!properties.IsHorizontalMouseWheel && properties.MouseWheelDelta != 0)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                if (properties.MouseWheelDelta > 0)
                {
                    ZoomInRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    ZoomOutRequested?.Invoke(this, EventArgs.Empty);
                }

                e.Handled = true;
            }
        }
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
            var top = (PageHeightPoints - rect.Top) * DisplayScale;
            var width = Math.Max(1, (rect.Right - rect.Left) * DisplayScale);
            var height = Math.Max(1, (rect.Top - rect.Bottom) * DisplayScale);

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
