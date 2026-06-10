using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using System.Collections.Generic;
using Windows.Foundation;
using ElliePdf.ViewModels;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;

namespace ElliePdf.Pages;

public sealed partial class EditPage : Page
{
    private EditPageViewModel? _viewModel;
    private readonly List<List<Point>> _strokes = new();
    private List<Point>? _currentStroke;
    private List<List<Point>> _signatureStrokes = new();
    private List<Point>? _currentSignatureStroke;

    public EditPage()
    {
        InitializeComponent();
        InitializeViewModel();
        WireUpEventHandlers();
    }

    private void InitializeViewModel()
    {
        _viewModel = App.AppHost.Services.GetRequiredService<EditPageViewModel>();
        this.DataContext = _viewModel;
    }

    private void WireUpEventHandlers()
    {
        // Main canvas for drawing and placing elements
        MainCanvas.PointerPressed += MainCanvas_PointerPressed;
        MainCanvas.PointerMoved += MainCanvas_PointerMoved;
        MainCanvas.PointerReleased += MainCanvas_PointerReleased;

        // Signature canvas
        SignatureCanvas.PointerPressed += SignatureCanvas_PointerPressed;
        SignatureCanvas.PointerMoved += SignatureCanvas_PointerMoved;
        SignatureCanvas.PointerReleased += SignatureCanvas_PointerReleased;

        // Button commands
        ToggleInkButton.Click += (s, e) =>
        {
            if (_viewModel != null)
            {
                _viewModel.ToggleInkModeCommand.Execute(null);
            }
        };

        BtnAddText.Click += BtnAddText_Click;
        BtnSignature.Click += BtnSignature_Click;
        BtnClearSignature.Click += BtnClearSignature_Click;

        // Dialog primary button (OK for signature)
        SignatureDialog.PrimaryButtonClick += SignatureDialog_PrimaryButtonClick;
        SignatureDialog.SecondaryButtonClick += SignatureDialog_SecondaryButtonClick;
    }

    // ========== Main Canvas Drawing ==========
    private void MainCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsInkModeEnabled)
            return;

        var p = e.GetCurrentPoint(MainCanvas).Position;
        _currentStroke = new List<Point> { p };
        _strokes.Add(_currentStroke);
        MainCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void MainCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_currentStroke is null || _viewModel == null || !_viewModel.IsInkModeEnabled)
            return;

        var p = e.GetCurrentPoint(MainCanvas).Position;
        _currentStroke.Add(p);

        // Redraw all strokes
        RefreshMainCanvasInk();
        e.Handled = true;
    }

    private void MainCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_currentStroke is null)
            return;

        MainCanvas.ReleasePointerCapture(e.Pointer);
        _currentStroke = null;
        e.Handled = true;
    }

    private void RefreshMainCanvasInk()
    {
        // Keep existing elements (text annotations and signatures), only redraw ink strokes
        var existingElements = MainCanvas.Children
            .Where(c => c is not Polyline)
            .ToList();

        MainCanvas.Children.Clear();

        // Redraw all ink strokes
        foreach (var stroke in _strokes)
        {
            var pointCollection = new Microsoft.UI.Xaml.Media.PointCollection();
            foreach (var point in stroke)
            {
                pointCollection.Add(point);
            }
            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(Colors.Black),
                StrokeThickness = 2,
                Points = pointCollection
            };
            MainCanvas.Children.Add(poly);
        }

        // Re-add existing elements
        foreach (var element in existingElements)
        {
            MainCanvas.Children.Add(element);
        }
    }

    // ========== Add Text Annotation ==========
    private void BtnAddText_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
            return;

        var textBox = new TextBox
        {
            Text = "Enter text here...",
            Width = 200,
            Height = 40
        };

        Canvas.SetLeft(textBox, 50);
        Canvas.SetTop(textBox, 50);

        MainCanvas.Children.Add(textBox);

        // Add to ViewModel
        _viewModel.AddTextAnnotationCommand.Execute(new Point(50, 50));
    }

    // ========== Signature Dialog ==========
    private async void BtnSignature_Click(object sender, RoutedEventArgs e)
    {
        _signatureStrokes.Clear();
        SignatureCanvas.Children.Clear();

        var result = await SignatureDialog.ShowAsync();
    }

    private void BtnClearSignature_Click(object sender, RoutedEventArgs e)
    {
        _signatureStrokes.Clear();
        SignatureCanvas.Children.Clear();
    }

    // ========== Signature Canvas Drawing ==========
    private void SignatureCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(SignatureCanvas).Position;
        _currentSignatureStroke = new List<Point> { p };
        _signatureStrokes.Add(_currentSignatureStroke);
        SignatureCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SignatureCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_currentSignatureStroke is null)
            return;

        var p = e.GetCurrentPoint(SignatureCanvas).Position;
        _currentSignatureStroke.Add(p);

        // Redraw all signature strokes
        RefreshSignatureCanvasInk();
        e.Handled = true;
    }

    private void SignatureCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_currentSignatureStroke is null)
            return;

        SignatureCanvas.ReleasePointerCapture(e.Pointer);
        _currentSignatureStroke = null;
        e.Handled = true;
    }

    private void RefreshSignatureCanvasInk()
    {
        SignatureCanvas.Children.Clear();
        foreach (var stroke in _signatureStrokes)
        {
            var pointCollection = new Microsoft.UI.Xaml.Media.PointCollection();
            foreach (var point in stroke)
            {
                pointCollection.Add(point);
            }
            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(Colors.Black),
                StrokeThickness = 2,
                Points = pointCollection
            };
            SignatureCanvas.Children.Add(poly);
        }
    }

    // ========== Signature Dialog Events ==========
    private void SignatureDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Convert signature to image stream and place on main canvas
        ConvertSignatureToImageAndPlaceOnCanvas();
    }

    private void SignatureDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _signatureStrokes.Clear();
        SignatureCanvas.Children.Clear();
    }

    private async void ConvertSignatureToImageAndPlaceOnCanvas()
    {
        try
        {
            // Create an in-memory bitmap of the signature
            var renderTargetBitmap = new RenderTargetBitmap();
            await renderTargetBitmap.RenderAsync(SignatureCanvas);

            // Convert to byte array
            var pixels = await renderTargetBitmap.GetPixelsAsync();
            var bytes = pixels.ToArray();

            // Create a base64 string from the image data
            var base64 = Convert.ToBase64String(bytes);

            if (_viewModel != null)
            {
                // Add signature to the ViewModel
                _viewModel.AddSignatureCommand.Execute(base64);

                // Create an image control to display the signature on the main canvas
                var image = new Image
                {
                    Width = 150,
                    Height = 75,
                    Stretch = Stretch.UniformToFill
                };

                // Create bitmap source from bytes
                var bitmapImage = new BitmapImage();
                
                // Create a stream from the pixels
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(pixels);
                    stream.Seek(0);
                    await bitmapImage.SetSourceAsync(stream);
                }
                
                image.Source = bitmapImage;

                Canvas.SetLeft(image, 100);
                Canvas.SetTop(image, 100);

                MainCanvas.Children.Add(image);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error converting signature: {ex.Message}");
        }
    }
}
