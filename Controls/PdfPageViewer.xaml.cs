using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

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

    public PdfPageViewer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        PageScrollViewer.PointerWheelChanged += OnPointerWheelChanged;
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

    public PdfEditSurface EditSurface => PageEditSurface;

    public event EventHandler<double>? ViewportWidthChanged;

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
            viewer.ReportViewportWidth();
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
        PageChrome.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        PageChrome.CornerRadius = new CornerRadius(4);
        PageScrollViewer.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        ApplyContentInset();
    }

    private void ApplyContentInset() =>
        PageChrome.Margin = new Thickness(0, 0, 0, Math.Max(0, ContentBottomInset));

    private void ApplyOverlayEnabled()
    {
        PageEditSurface.IsHitTestVisible = IsOverlayEnabled;
        PageEditSurface.Visibility = IsOverlayEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PageScrollViewer.PointerPressed += OnPagePointerPressed;
        ReportViewportWidth();
    }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e) =>
        PagePointerPressed?.Invoke(this, EventArgs.Empty);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ReportViewportWidth();

    private void ReportViewportWidth()
    {
        var chromePadding = IsChromeless ? 0 : 48;
        var width = Math.Max(0, PageScrollViewer.ActualWidth - chromePadding);
        ViewportWidthChanged?.Invoke(this, width);
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
}
