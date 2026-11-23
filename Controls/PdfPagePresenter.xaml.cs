using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ElliePdf.ViewModels;
using ElliePdf.Semantics;

namespace ElliePdf.Controls;

public sealed partial class PdfPagePresenter : UserControl
{
    public static readonly DependencyProperty PageTilesProperty =
        DependencyProperty.Register(
            nameof(PageTiles),
            typeof(object),
            typeof(PdfPagePresenter),
            new PropertyMetadata(null, OnPageTilesChanged));

    public static readonly DependencyProperty SearchHighlightsProperty =
        DependencyProperty.Register(
            nameof(SearchHighlights),
            typeof(IReadOnlyList<PdfRect>),
            typeof(PdfPagePresenter),
            new PropertyMetadata(null, OnHighlightPropertyChanged));

    public static readonly DependencyProperty PageHeightPointsProperty =
        DependencyProperty.Register(
            nameof(PageHeightPoints),
            typeof(float),
            typeof(PdfPagePresenter),
            new PropertyMetadata(0f, OnHighlightPropertyChanged));

    public static readonly DependencyProperty DisplayScaleProperty =
        DependencyProperty.Register(
            nameof(DisplayScale),
            typeof(double),
            typeof(PdfPagePresenter),
            new PropertyMetadata(1.0, OnHighlightPropertyChanged));

    public static readonly DependencyProperty SemanticPageProperty =
        DependencyProperty.Register(
            nameof(SemanticPage),
            typeof(object),
            typeof(PdfPagePresenter),
            new PropertyMetadata(null, OnSemanticPropertyChanged));

    public static readonly DependencyProperty CanCopyProperty =
        DependencyProperty.Register(
            nameof(CanCopy),
            typeof(bool),
            typeof(PdfPagePresenter),
            new PropertyMetadata(false, OnSemanticPropertyChanged));

    public PdfPagePresenter()
    {
        InitializeComponent();
        TileSurface.SizeChanged += (_, _) => UpdateSearchHighlights();
        SemanticOverlay.LinkInvoked += (_, args) => LinkInvoked?.Invoke(this, args);
        SemanticOverlay.FormValueCommitted += (_, args) => FormValueCommitted?.Invoke(this, args);
        SemanticOverlay.PushButtonInvoked += (_, args) => PushButtonInvoked?.Invoke(this, args);
    }

    public IEnumerable<RenderedTileViewModel>? PageTiles
    {
        get => GetValue(PageTilesProperty) as IEnumerable<RenderedTileViewModel>;
        set => SetValue(PageTilesProperty, value);
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

    public event EventHandler<SemanticLinkInvokedEventArgs>? LinkInvoked;

    public event EventHandler<SemanticFormValueEventArgs>? FormValueCommitted;

    public event EventHandler<SemanticPushButtonInvokedEventArgs>? PushButtonInvoked;

    private static void OnPageTilesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPagePresenter presenter)
        {
            presenter.TileSurface.Tiles = e.NewValue as IEnumerable<RenderedTileViewModel>;
        }
    }

    private static void OnHighlightPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPagePresenter presenter)
        {
            presenter.UpdateSearchHighlights();
            presenter.SemanticOverlay.PageHeightPoints = presenter.PageHeightPoints;
            presenter.SemanticOverlay.DisplayScale = presenter.DisplayScale;
        }
    }

    private static void OnSemanticPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PdfPagePresenter presenter) return;
        presenter.SemanticOverlay.SemanticPage = presenter.SemanticPage;
        presenter.SemanticOverlay.CanCopy = presenter.CanCopy;
        presenter.SemanticOverlay.PageHeightPoints = presenter.PageHeightPoints;
        presenter.SemanticOverlay.DisplayScale = presenter.DisplayScale;
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
            var rectangle = new Rectangle
            {
                Fill = highlightBrush,
                Width = Math.Max(1, (rect.Right - rect.Left) * DisplayScale),
                Height = Math.Max(1, (rect.Bottom - rect.Top) * DisplayScale),
                RadiusX = 2,
                RadiusY = 2
            };

            SearchHighlightCanvas.Children.Add(rectangle);
            Canvas.SetLeft(rectangle, rect.Left * DisplayScale);
            Canvas.SetTop(rectangle, (PageHeightPoints - rect.Bottom) * DisplayScale);
        }
    }
}
