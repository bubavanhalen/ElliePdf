using ElliePdf.Models;
using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ElliePdf.Pages;

public sealed partial class EditPage : Page
{
    private readonly List<List<Point>> _activeInkStrokes = [];
    private List<Point>? _currentStroke;
    public EditPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<EditPageViewModel>();
        DataContext = ViewModel;
        Loaded += OnLoaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        BtnClearSignature.Click += BtnClearSignature_Click;
        SignatureDialog.PrimaryButtonClick += SignatureDialog_PrimaryButtonClick;
    }

    public EditPageViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditPageViewModel.PageImage) or nameof(EditPageViewModel.CurrentOverlay))
        {
            SyncOverlayCanvasSize();
            RedrawOverlay();
        }
    }

    private void PageHost_SizeChanged(object sender, SizeChangedEventArgs e) => SyncOverlayCanvasSize();

    private void SyncOverlayCanvasSize()
    {
        OverlayCanvas.Width = ViewModel.PagePixelWidth;
        OverlayCanvas.Height = ViewModel.PagePixelHeight;
    }

    private void RedrawOverlay()
    {
        OverlayCanvas.Children.Clear();
        _activeInkStrokes.Clear();
        var overlay = ViewModel.CurrentOverlay;

        foreach (var stroke in overlay.InkStrokes)
        {
            var points = stroke.Points.Select(ToCanvasPoint).ToList();
            _activeInkStrokes.Add(points);
            OverlayCanvas.Children.Add(CreatePolyline(points));
        }

        foreach (var text in overlay.TextItems)
        {
            var box = new TextBox
            {
                Text = text.Text,
                FontSize = text.FontSize,
                Width = 220
            };
            box.LostFocus += (_, _) => PersistOverlayFromCanvas();
            Canvas.SetLeft(box, ToCanvasPoint(new PointOverlay { X = text.X, Y = text.Y }).X);
            Canvas.SetTop(box, ToCanvasPoint(new PointOverlay { X = text.X, Y = text.Y }).Y);
            OverlayCanvas.Children.Add(box);
        }

        foreach (var signature in overlay.Signatures)
        {
            if (TryCreateSignatureImage(signature, out var image))
            {
                Canvas.SetLeft(image, ToCanvasPoint(new PointOverlay { X = signature.X, Y = signature.Y }).X);
                Canvas.SetTop(image, ToCanvasPoint(new PointOverlay { X = signature.X, Y = signature.Y }).Y);
                OverlayCanvas.Children.Add(image);
            }
        }
    }

    private void OverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsInkModeEnabled)
        {
            return;
        }

        var point = e.GetCurrentPoint(OverlayCanvas).Position;
        _currentStroke = [point];
        _activeInkStrokes.Add(_currentStroke);
        OverlayCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OverlayCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_currentStroke is null || !ViewModel.IsInkModeEnabled)
        {
            return;
        }

        _currentStroke.Add(e.GetCurrentPoint(OverlayCanvas).Position);
        RedrawInkOnly();
        e.Handled = true;
    }

    private void OverlayCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_currentStroke is null)
        {
            return;
        }

        OverlayCanvas.ReleasePointerCapture(e.Pointer);
        _currentStroke = null;
        PersistOverlayFromCanvas();
        e.Handled = true;
    }

    private void AddTextButton_Click(object sender, RoutedEventArgs e)
    {
        var overlay = ViewModel.CurrentOverlay;
        overlay.TextItems.Add(new TextOverlay { X = 72, Y = 72, Text = "Enter text..." });
        ViewModel.PersistCurrentOverlay(overlay);
        RedrawOverlay();
    }

    private async void SignatureButton_Click(object sender, RoutedEventArgs e)
    {
        SignatureCanvas.Children.Clear();
        await SignatureDialog.ShowAsync();
    }

    private void BtnClearSignature_Click(object sender, RoutedEventArgs e) =>
        SignatureCanvas.Children.Clear();

    private void SignatureDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _ = PlaceSignatureAsync();
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
        var base64 = Convert.ToBase64String(memory.ToArray());

        var overlay = ViewModel.CurrentOverlay;
        overlay.Signatures.Add(new SignatureOverlay
        {
            X = 100,
            Y = 100,
            ImageBase64 = base64,
            Width = 150,
            Height = 75
        });
        ViewModel.PersistCurrentOverlay(overlay);
        RedrawOverlay();
    }

    private void RedrawInkOnly()
    {
        var keep = OverlayCanvas.Children.Where(child => child is not Polyline).ToList();
        OverlayCanvas.Children.Clear();
        foreach (var stroke in _activeInkStrokes)
        {
            OverlayCanvas.Children.Add(CreatePolyline(stroke));
        }

        foreach (var child in keep)
        {
            OverlayCanvas.Children.Add(child);
        }
    }

    private void PersistOverlayFromCanvas()
    {
        var overlay = new PageOverlayState
        {
            InkStrokes = _activeInkStrokes
                .Select(stroke => new InkStrokeOverlay
                {
                    Points = stroke.Select(ToPagePoint).ToList()
                })
                .ToList(),
            TextItems = OverlayCanvas.Children
                .OfType<TextBox>()
                .Select(box => new TextOverlay
                {
                    X = Canvas.GetLeft(box) / ViewModel.DisplayScale,
                    Y = Canvas.GetTop(box) / ViewModel.DisplayScale,
                    Text = box.Text,
                    FontSize = box.FontSize
                })
                .ToList(),
            Signatures = ViewModel.CurrentOverlay.Signatures
        };

        ViewModel.PersistCurrentOverlay(overlay);
    }

    private Point ToCanvasPoint(PointOverlay point) =>
        new(point.X * ViewModel.DisplayScale, point.Y * ViewModel.DisplayScale);

    private PointOverlay ToPagePoint(Point point) =>
        new() { X = point.X / ViewModel.DisplayScale, Y = point.Y / ViewModel.DisplayScale };

    private static Polyline CreatePolyline(IReadOnlyList<Point> points)
    {
        var collection = new PointCollection();
        foreach (var point in points)
        {
            collection.Add(point);
        }

        return new Polyline
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Black),
            StrokeThickness = 2,
            Points = collection
        };
    }

    private static bool TryCreateSignatureImage(SignatureOverlay signature, out Image image)
    {
        image = new Image
        {
            Width = signature.Width,
            Height = signature.Height,
            Stretch = Stretch.Uniform
        };

        try
        {
            var bytes = Convert.FromBase64String(signature.ImageBase64);
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            stream.WriteAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            bitmap.SetSourceAsync(stream).AsTask().GetAwaiter().GetResult();
            image.Source = bitmap;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
