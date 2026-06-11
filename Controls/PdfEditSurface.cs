using ElliePdf.Models;
using ElliePdf.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Text;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Storage.Streams;
using TextFontStyle = Windows.UI.Text.FontStyle;

namespace ElliePdf.Controls;

public sealed class PdfEditSurface : Canvas
{
    private enum SelectionKind
    {
        None,
        Text,
        Signature,
        Ink
    }

    private enum DragMode
    {
        None,
        Move,
        Resize
    }

    private readonly List<List<Point>> _activeInkStrokes = [];
    private readonly List<PageOverlayState> _undoStack = [];
    private readonly Border _selectionAdorner;
    private readonly Border _resizeHandle;
    private readonly Border _textToolbar;
    private readonly Button _textColorButton;
    private List<Point>? _currentStroke;
    private DragMode _dragMode;
    private Point _dragStart;
    private double _startLeft;
    private double _startTop;
    private double _startWidth;
    private double _startHeight;
    private SelectionKind _selectionKind;
    private string? _selectedId;
    private bool _isRendering;
    private bool _isPlacingText;
    private static readonly InputCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static readonly InputCursor MoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputCursor TextCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);

    public PdfEditSurface()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        IsTabStop = true;

        _selectionAdorner = new Border
        {
            BorderBrush = new SolidColorBrush(Colors.DodgerBlue),
            BorderThickness = new Thickness(1.5),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        _resizeHandle = new Border
        {
            Width = 12,
            Height = 12,
            Background = new SolidColorBrush(Colors.DodgerBlue),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed
        };
        _resizeHandle.PointerPressed += ResizeHandle_PointerPressed;
        _resizeHandle.PointerMoved += ResizeHandle_PointerMoved;
        _resizeHandle.PointerReleased += ResizeHandle_PointerReleased;

        _textColorButton = CreateToolbarButton(string.Empty);
        _textColorButton.Width = 28;
        _textColorButton.Content = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Colors.Black)
        };
        _textColorButton.Flyout = CreateColorFlyout();

        _textToolbar = new Border
        {
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 32, 32, 32)),
            CornerRadius = new CornerRadius(14),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    CreateToolbarButton("N", (_, _) => ApplySelectedTextStyle(isBold: false, isItalic: false)),
                    CreateToolbarButton("B", (_, _) => ApplySelectedTextStyle(isBold: true, isItalic: false)),
                    CreateToolbarButton("I", (_, _) => ApplySelectedTextStyle(isBold: false, isItalic: true)),
                    _textColorButton
                }
            }
        };

        Children.Add(_selectionAdorner);
        Children.Add(_resizeHandle);
        Children.Add(_textToolbar);
        PointerPressed += Surface_PointerPressed;
        PointerMoved += Surface_PointerMoved;
        PointerReleased += Surface_PointerReleased;
        KeyDown += Surface_KeyDown;
    }

    public PageOverlayState Overlay { get; private set; } = new();

    public ReaderEditTool ActiveTool { get; set; } = ReaderEditTool.Select;

    public double DisplayScale { get; set; } = 1.0;

    public string InkColorHex { get; set; } = "#000000";

    public double InkThickness { get; set; } = 2;

    public event EventHandler<PageOverlayState>? OverlayChanged;

    public event EventHandler<ReaderEditTool>? ActiveToolChangeRequested;

    public void LoadOverlay(PageOverlayState overlay, double displayScale, double width, double height)
    {
        Overlay = CloneOverlay(overlay);
        DisplayScale = displayScale <= 0 ? 1.0 : displayScale;
        Width = width;
        Height = height;
        ClearSelection();
        RenderOverlay();
    }

    public void CommitActiveEdits() => PersistTextBoxes();

    public void PlaceText()
    {
        BeginTextPlacement(new Point(Math.Max(24, Width / 2 - 110), Math.Max(24, Height / 2 - 22)), null);
    }

    public void PlaceSignature(string imageBase64)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return;
        }

        PushUndo();
        var signature = new SignatureOverlay
        {
            X = Math.Max(24, (Width / DisplayScale - 150) / 2),
            Y = Math.Max(24, (Height / DisplayScale - 75) / 2),
            ImageBase64 = imageBase64,
            Width = 150,
            Height = 75
        };
        Overlay.Signatures.Add(signature);
        RenderOverlay();
        SelectItem(SelectionKind.Signature, signature.Id);
        NotifyOverlayChanged();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        Overlay = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        ClearSelection();
        RenderOverlay();
        NotifyOverlayChanged(pushUndo: false);
    }

    public void DeleteSelection()
    {
        if (_selectionKind == SelectionKind.None || _selectedId is null)
        {
            return;
        }

        PushUndo();
        if (_selectionKind == SelectionKind.Text)
        {
            Overlay.TextItems.RemoveAll(item => item.Id == _selectedId);
        }
        else if (_selectionKind == SelectionKind.Signature)
        {
            Overlay.Signatures.RemoveAll(item => item.Id == _selectedId);
        }
        else if (_selectionKind == SelectionKind.Ink && int.TryParse(_selectedId, out var index) && index >= 0 && index < Overlay.InkStrokes.Count)
        {
            Overlay.InkStrokes.RemoveAt(index);
        }

        ClearSelection();
        RenderOverlay();
        NotifyOverlayChanged();
    }

    public void ClearSelection()
    {
        _selectionKind = SelectionKind.None;
        _selectedId = null;
        _selectionAdorner.Visibility = Visibility.Collapsed;
        _resizeHandle.Visibility = Visibility.Collapsed;
        _textToolbar.Visibility = Visibility.Collapsed;
    }

    private void BeginTextPlacement(Point canvasPoint, Pointer? pointer)
    {
        PushUndo();
        var text = new TextOverlay
        {
            X = canvasPoint.X / DisplayScale,
            Y = canvasPoint.Y / DisplayScale,
            Text = "Enter text...",
            Width = 160,
            Height = 40
        };

        Overlay.TextItems.Add(text);
        RenderOverlay();
        SelectItem(SelectionKind.Text, text.Id);
        NotifyOverlayChanged(pushUndo: false);

        if (TryGetSelectedElement() is not { } selected)
        {
            return;
        }

        _dragMode = DragMode.Resize;
        _isPlacingText = true;
        _dragStart = canvasPoint;
        _startWidth = selected.Width;
        _startHeight = selected.Height;
        if (pointer is not null)
        {
            CapturePointer(pointer);
        }

        if (selected is TextBox box)
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        }
    }

    private void RenderOverlay()
    {
        _isRendering = true;
        try
        {
            Children.Clear();
            _activeInkStrokes.Clear();

            for (var index = 0; index < Overlay.InkStrokes.Count; index++)
            {
                var stroke = Overlay.InkStrokes[index];
                var points = stroke.Points.Select(ToCanvasPoint).ToList();
                _activeInkStrokes.Add(points);
                var polyline = CreatePolyline(points, stroke.ColorHex, stroke.Thickness * DisplayScale);
                polyline.Tag = ("ink", index.ToString());
                polyline.PointerPressed += Selectable_PointerPressed;
                Children.Add(polyline);
            }

            foreach (var text in Overlay.TextItems)
            {
                var box = new TextBox
                {
                    Text = text.Text,
                    FontSize = text.FontSize * DisplayScale,
                    Width = Math.Max(80, text.Width * DisplayScale),
                    Height = Math.Max(32, text.Height * DisplayScale),
                    Foreground = ColorBrushFromHex(text.ColorHex),
                    FontWeight = text.IsBold ? FontWeights.SemiBold : FontWeights.Normal,
                    FontStyle = text.IsItalic ? TextFontStyle.Italic : TextFontStyle.Normal,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(2),
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    Tag = ("text", text.Id)
                };
                ApplyTextBoxChrome(box);
                box.TextChanged += TextBox_TextChanged;
                box.PointerPressed += Selectable_PointerPressed;
                box.PointerEntered += TextBox_PointerEntered;
                box.PointerExited += TextBox_PointerExited;
                Canvas.SetLeft(box, text.X * DisplayScale);
                Canvas.SetTop(box, text.Y * DisplayScale);
                Children.Add(box);
            }

            foreach (var signature in Overlay.Signatures)
            {
                if (TryCreateSignatureImage(signature, out var image))
                {
                    image.Width = Math.Max(40, signature.Width * DisplayScale);
                    image.Height = Math.Max(24, signature.Height * DisplayScale);
                    image.Tag = ("signature", signature.Id);
                    image.PointerPressed += Selectable_PointerPressed;
                    Canvas.SetLeft(image, signature.X * DisplayScale);
                    Canvas.SetTop(image, signature.Y * DisplayScale);
                    Children.Add(image);
                }
            }

            Children.Add(_selectionAdorner);
            Children.Add(_resizeHandle);
            Children.Add(_textToolbar);
            RestoreSelectionAdorner();
        }
        finally
        {
            _isRendering = false;
        }
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);

        if (ActiveTool == ReaderEditTool.Ink)
        {
            PushUndo();
            var point = e.GetCurrentPoint(this).Position;
            _currentStroke = [point];
            _activeInkStrokes.Add(_currentStroke);
            CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (ActiveTool == ReaderEditTool.Eraser)
        {
            TryEraseAt(e.GetCurrentPoint(this).Position);
            e.Handled = true;
            return;
        }

        if (ActiveTool == ReaderEditTool.Text && ReferenceEquals(e.OriginalSource, this))
        {
            BeginTextPlacement(e.GetCurrentPoint(this).Position, e.Pointer);
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(e.OriginalSource, this))
        {
            ClearSelection();
        }
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.Move && TryGetSelectedElement() is { } selected)
        {
            var point = e.GetCurrentPoint(this).Position;
            Canvas.SetLeft(selected, Math.Max(0, _startLeft + point.X - _dragStart.X));
            Canvas.SetTop(selected, Math.Max(0, _startTop + point.Y - _dragStart.Y));
            UpdateSelectionAdorner(selected);
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.Resize && TryGetSelectedElement() is { } resizing)
        {
            var point = e.GetCurrentPoint(this).Position;
            resizing.Width = Math.Max(48, _startWidth + point.X - _dragStart.X);
            resizing.Height = Math.Max(28, _startHeight + point.Y - _dragStart.Y);
            UpdateSelectionAdorner(resizing);
            e.Handled = true;
            return;
        }

        if (_currentStroke is null || ActiveTool != ReaderEditTool.Ink)
        {
            return;
        }

        _currentStroke.Add(e.GetCurrentPoint(this).Position);
        RedrawInkOnly();
        e.Handled = true;
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.Move && TryGetSelectedElement() is not null)
        {
            ReleasePointerCapture(e.Pointer);
            _dragMode = DragMode.None;
            PersistSelectedElement();
            NotifyOverlayChanged(pushUndo: false);
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.Resize && TryGetSelectedElement() is not null)
        {
            ReleasePointerCapture(e.Pointer);
            _dragMode = DragMode.None;
            PersistSelectedElement();
            NotifyOverlayChanged(pushUndo: false);
            e.Handled = true;
            return;
        }

        if (_currentStroke is null)
        {
            return;
        }

        ReleasePointerCapture(e.Pointer);
        var stroke = new InkStrokeOverlay
        {
            ColorHex = InkColorHex,
            Thickness = InkThickness,
            Points = _currentStroke.Select(ToPagePoint).ToList()
        };
        if (stroke.Points.Count > 1)
        {
            Overlay.InkStrokes.Add(stroke);
        }

        _currentStroke = null;
        RenderOverlay();
        NotifyOverlayChanged(pushUndo: false);
        e.Handled = true;
    }

    private void Selectable_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ActiveTool == ReaderEditTool.Eraser)
        {
            if (sender is FrameworkElement element)
            {
                SelectFromTag(element.Tag);
                DeleteSelection();
            }

            e.Handled = true;
            return;
        }

        if (ActiveTool is not ReaderEditTool.Select and not ReaderEditTool.Text and not ReaderEditTool.Signature)
        {
            return;
        }

        if (sender is not FrameworkElement selected || !SelectFromTag(selected.Tag))
        {
            return;
        }

        CommitActiveEdits();
        PushUndo();
        _dragMode = DragMode.Move;
        _dragStart = e.GetCurrentPoint(this).Position;
        _startLeft = Canvas.GetLeft(selected);
        _startTop = Canvas.GetTop(selected);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TryGetSelectedElement() is not { } selected)
        {
            return;
        }

        PushUndo();
        _dragMode = DragMode.Resize;
        _dragStart = e.GetCurrentPoint(this).Position;
        _startWidth = selected.ActualWidth > 0 ? selected.ActualWidth : selected.Width;
        _startHeight = selected.ActualHeight > 0 ? selected.ActualHeight : selected.Height;
        _resizeHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.Resize || TryGetSelectedElement() is not { } selected)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Position;
        selected.Width = Math.Max(40, _startWidth + point.X - _dragStart.X);
        selected.Height = Math.Max(24, _startHeight + point.Y - _dragStart.Y);
        UpdateSelectionAdorner(selected);
        e.Handled = true;
    }

    private void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.Resize)
        {
            return;
        }

        _resizeHandle.ReleasePointerCapture(e.Pointer);
        _dragMode = DragMode.None;
        PersistSelectedElement();
        NotifyOverlayChanged(pushUndo: false);
        e.Handled = true;
    }

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRendering || sender is not TextBox box || box.Tag is not ValueTuple<string, string> tag || tag.Item1 != "text")
        {
            return;
        }

        var text = Overlay.TextItems.FirstOrDefault(item => item.Id == tag.Item2);
        if (text is null)
        {
            return;
        }

        text.Text = box.Text;
        text.Width = Math.Max(40, box.Width / DisplayScale);
        text.Height = Math.Max(24, box.Height / DisplayScale);
        NotifyOverlayChanged();
    }

    private void Surface_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            DeleteSelection();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    private bool SelectFromTag(object? tag)
    {
        if (tag is not ValueTuple<string, string> typedTag)
        {
            return false;
        }

        var kind = typedTag.Item1 switch
        {
            "text" => SelectionKind.Text,
            "signature" => SelectionKind.Signature,
            "ink" => SelectionKind.Ink,
            _ => SelectionKind.None
        };

        if (kind == SelectionKind.None)
        {
            return false;
        }

        SelectItem(kind, typedTag.Item2);
        return true;
    }

    private void SelectItem(SelectionKind kind, string id)
    {
        _selectionKind = kind;
        _selectedId = id;
        RestoreSelectionAdorner();
    }

    private void RestoreSelectionAdorner()
    {
        if (TryGetSelectedElement() is { } selected)
        {
            UpdateSelectionAdorner(selected);
        }
        else
        {
            _selectionAdorner.Visibility = Visibility.Collapsed;
            _resizeHandle.Visibility = Visibility.Collapsed;
        }
    }

    private FrameworkElement? TryGetSelectedElement()
    {
        if (_selectionKind == SelectionKind.None || _selectedId is null)
        {
            return null;
        }

        return Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(element =>
                element.Tag is ValueTuple<string, string> tag &&
                tag.Item2 == _selectedId &&
                ((_selectionKind == SelectionKind.Text && tag.Item1 == "text") ||
                 (_selectionKind == SelectionKind.Signature && tag.Item1 == "signature") ||
                 (_selectionKind == SelectionKind.Ink && tag.Item1 == "ink")));
    }

    private void UpdateSelectionAdorner(FrameworkElement selected)
    {
        var left = Canvas.GetLeft(selected);
        var top = Canvas.GetTop(selected);
        var width = selected.ActualWidth > 0 ? selected.ActualWidth : selected.Width;
        var height = selected.ActualHeight > 0 ? selected.ActualHeight : selected.Height;

        if (double.IsNaN(left) || double.IsNaN(top) || width <= 0 || height <= 0)
        {
            return;
        }

        _selectionAdorner.Width = width;
        _selectionAdorner.Height = height;
        Canvas.SetLeft(_selectionAdorner, left);
        Canvas.SetTop(_selectionAdorner, top);
        _selectionAdorner.Visibility = Visibility.Visible;

        _resizeHandle.Visibility = _selectionKind is SelectionKind.Text or SelectionKind.Signature
            ? Visibility.Visible
            : Visibility.Collapsed;
        Canvas.SetLeft(_resizeHandle, left + width - _resizeHandle.Width / 2);
        Canvas.SetTop(_resizeHandle, top + height - _resizeHandle.Height / 2);

        if (_selectionKind == SelectionKind.Text && _selectedId is not null)
        {
            UpdateTextToolbar(left, top, width);
        }
        else
        {
            _textToolbar.Visibility = Visibility.Collapsed;
        }
    }

    private void PersistSelectedElement()
    {
        if (TryGetSelectedElement() is not { } selected || _selectedId is null)
        {
            return;
        }

        var left = Canvas.GetLeft(selected) / DisplayScale;
        var top = Canvas.GetTop(selected) / DisplayScale;
        var width = Math.Max(40, selected.Width / DisplayScale);
        var height = Math.Max(24, selected.Height / DisplayScale);

        if (_selectionKind == SelectionKind.Text)
        {
            var text = Overlay.TextItems.FirstOrDefault(item => item.Id == _selectedId);
            if (text is not null)
            {
                text.X = left;
                text.Y = top;
                text.Width = width;
                text.Height = height;
                if (selected is TextBox box)
                {
                    text.Text = box.Text;
                    text.FontSize = Math.Max(8, box.FontSize / DisplayScale);
                    text.IsBold = box.FontWeight.Weight >= FontWeights.SemiBold.Weight;
                    text.IsItalic = box.FontStyle == TextFontStyle.Italic;
                }
            }
        }
        else if (_selectionKind == SelectionKind.Signature)
        {
            var signature = Overlay.Signatures.FirstOrDefault(item => item.Id == _selectedId);
            if (signature is not null)
            {
                signature.X = left;
                signature.Y = top;
                signature.Width = width;
                signature.Height = height;
            }
        }
    }

    private void PersistTextBoxes()
    {
        foreach (var box in Children.OfType<TextBox>())
        {
            if (box.Tag is not ValueTuple<string, string> tag || tag.Item1 != "text")
            {
                continue;
            }

            var text = Overlay.TextItems.FirstOrDefault(item => item.Id == tag.Item2);
            if (text is null)
            {
                continue;
            }

            text.X = Canvas.GetLeft(box) / DisplayScale;
            text.Y = Canvas.GetTop(box) / DisplayScale;
            text.Text = box.Text;
            text.FontSize = Math.Max(8, box.FontSize / DisplayScale);
            text.Width = Math.Max(40, box.Width / DisplayScale);
            text.Height = Math.Max(24, box.Height / DisplayScale);
            text.IsBold = box.FontWeight.Weight >= FontWeights.SemiBold.Weight;
            text.IsItalic = box.FontStyle == TextFontStyle.Italic;
        }

        NotifyOverlayChanged();
    }

    private void UpdateTextToolbar(double selectedLeft, double selectedTop, double selectedWidth)
    {
        var text = Overlay.TextItems.FirstOrDefault(item => item.Id == _selectedId);
        if (text is null)
        {
            _textToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        if (_textColorButton.Content is Border dot)
        {
            dot.Background = ColorBrushFromHex(text.ColorHex);
        }

        _textToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = _textToolbar.DesiredSize.Width;
        var left = Math.Max(0, selectedLeft + selectedWidth / 2 - toolbarWidth / 2);
        var top = selectedTop - 36;
        if (top < 0)
        {
            top = selectedTop + 8;
        }

        Canvas.SetLeft(_textToolbar, left);
        Canvas.SetTop(_textToolbar, top);
        _textToolbar.Visibility = Visibility.Visible;
    }

    private void ApplySelectedTextStyle(bool isBold, bool isItalic)
    {
        if (_selectionKind != SelectionKind.Text || _selectedId is null)
        {
            return;
        }

        PushUndo();
        var text = Overlay.TextItems.FirstOrDefault(item => item.Id == _selectedId);
        if (text is null)
        {
            return;
        }

        text.IsBold = isBold;
        text.IsItalic = isItalic;

        if (TryGetSelectedElement() is TextBox box)
        {
            box.FontWeight = isBold ? FontWeights.SemiBold : FontWeights.Normal;
            box.FontStyle = isItalic ? TextFontStyle.Italic : TextFontStyle.Normal;
        }

        NotifyOverlayChanged(pushUndo: false);
    }

    private void ApplySelectedTextColor(Windows.UI.Color color)
    {
        if (_selectionKind != SelectionKind.Text || _selectedId is null)
        {
            return;
        }

        PushUndo();
        var text = Overlay.TextItems.FirstOrDefault(item => item.Id == _selectedId);
        if (text is null)
        {
            return;
        }

        text.ColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (TryGetSelectedElement() is TextBox box)
        {
            box.Foreground = new SolidColorBrush(color);
        }

        if (_textColorButton.Content is Border dot)
        {
            dot.Background = new SolidColorBrush(color);
        }

        NotifyOverlayChanged(pushUndo: false);
    }

    private void TryEraseAt(Point point)
    {
        var hit = Children
            .OfType<FrameworkElement>()
            .LastOrDefault(element =>
                element.Tag is ValueTuple<string, string> tag &&
                tag.Item1 != "ink" &&
                IsPointInsideElement(point, element));

        if (hit is not null)
        {
            SelectFromTag(hit.Tag);
            DeleteSelection();
            return;
        }

        for (var index = Overlay.InkStrokes.Count - 1; index >= 0; index--)
        {
            var points = Overlay.InkStrokes[index].Points.Select(ToCanvasPoint).ToArray();
            if (IsPointNearStroke(point, points, Math.Max(8, Overlay.InkStrokes[index].Thickness * DisplayScale + 4)))
            {
                PushUndo();
                Overlay.InkStrokes.RemoveAt(index);
                ClearSelection();
                RenderOverlay();
                NotifyOverlayChanged(pushUndo: false);
                return;
            }
        }
    }

    private static bool IsPointInsideElement(Point point, FrameworkElement element)
    {
        var left = Canvas.GetLeft(element);
        var top = Canvas.GetTop(element);
        var width = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
        var height = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
        return point.X >= left && point.X <= left + width && point.Y >= top && point.Y <= top + height;
    }

    private static bool IsPointNearStroke(Point point, IReadOnlyList<Point> points, double tolerance)
    {
        if (points.Count < 2)
        {
            return false;
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (DistanceToSegment(point, points[i - 1], points[i]) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
        {
            return Math.Sqrt(Math.Pow(point.X - start.X, 2) + Math.Pow(point.Y - start.Y, 2));
        }

        var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        var projection = new Point(start.X + t * dx, start.Y + t * dy);
        return Math.Sqrt(Math.Pow(point.X - projection.X, 2) + Math.Pow(point.Y - projection.Y, 2));
    }

    private void RedrawInkOnly()
    {
        var keep = Children.Where(child => child is not Polyline || ReferenceEquals(child, _selectionAdorner)).ToList();
        Children.Clear();

        foreach (var stroke in _activeInkStrokes)
        {
            Children.Add(CreatePolyline(stroke, InkColorHex, InkThickness * DisplayScale));
        }

        foreach (var child in keep)
        {
            Children.Add(child);
        }
    }

    private Point ToCanvasPoint(PointOverlay point) =>
        new(point.X * DisplayScale, point.Y * DisplayScale);

    private PointOverlay ToPagePoint(Point point) =>
        new() { X = point.X / DisplayScale, Y = point.Y / DisplayScale };

    private static Polyline CreatePolyline(IReadOnlyList<Point> points, string colorHex, double thickness)
    {
        var collection = new PointCollection();
        foreach (var point in points)
        {
            collection.Add(point);
        }

        return new Polyline
        {
            Stroke = ColorBrushFromHex(colorHex),
            StrokeThickness = Math.Max(1, thickness),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Points = collection
        };
    }

    private static bool TryCreateSignatureImage(SignatureOverlay signature, out Image image)
    {
        image = new Image
        {
            Stretch = Stretch.Fill
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

    private void PushUndo()
    {
        _undoStack.Add(CloneOverlay(Overlay));
        if (_undoStack.Count > 50)
        {
            _undoStack.RemoveAt(0);
        }
    }

    private void NotifyOverlayChanged(bool pushUndo = true)
    {
        OverlayChanged?.Invoke(this, CloneOverlay(Overlay));
    }

    private static PageOverlayState CloneOverlay(PageOverlayState source) =>
        new()
        {
            InkStrokes = source.InkStrokes
                .Select(stroke => new InkStrokeOverlay
                {
                    ColorHex = stroke.ColorHex,
                    Thickness = stroke.Thickness,
                    Points = stroke.Points.Select(point => new PointOverlay { X = point.X, Y = point.Y }).ToList()
                })
                .ToList(),
            TextItems = source.TextItems
                .Select(text => new TextOverlay
                {
                    Id = text.Id,
                    X = text.X,
                    Y = text.Y,
                    Text = text.Text,
                    FontSize = text.FontSize,
                    Width = text.Width,
                    Height = text.Height,
                    ColorHex = text.ColorHex,
                    IsBold = text.IsBold,
                    IsItalic = text.IsItalic
                })
                .ToList(),
            Signatures = source.Signatures
                .Select(signature => new SignatureOverlay
                {
                    Id = signature.Id,
                    X = signature.X,
                    Y = signature.Y,
                    ImageBase64 = signature.ImageBase64,
                    Width = signature.Width,
                    Height = signature.Height
                })
                .ToList()
        };

    private Button CreateToolbarButton(string text, RoutedEventHandler? click = null)
    {
        var button = new Button
        {
            Content = text,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(0)
        };

        if (click is not null)
        {
            button.Click += click;
        }

        return button;
    }

    private Flyout CreateColorFlyout()
    {
        var picker = new ColorPicker
        {
            Color = Colors.Black,
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            Width = 280
        };
        picker.ColorChanged += (_, args) => ApplySelectedTextColor(args.NewColor);

        return new Flyout
        {
            Content = picker
        };
    }

    private static SolidColorBrush ColorBrushFromHex(string colorHex) =>
        new(ParseColor(colorHex));

    private static Windows.UI.Color ParseColor(string colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return Colors.Black;
        }

        var hex = colorHex.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }

        return Colors.Black;
    }
}
