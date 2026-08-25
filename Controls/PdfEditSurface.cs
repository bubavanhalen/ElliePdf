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

public sealed partial class PdfEditSurface : Canvas
{
    private const string InkTagKind = "ink";
    private const string TextTagKind = "text";
    private const string SignatureTagKind = "signature";

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

    private sealed record OverlayTag(string Kind, string Id);

    private readonly List<PageOverlayState> _undoStack = [];
    private readonly Border _selectionAdorner;
    private readonly Border _resizeHandle;
    private readonly Border _textToolbar;
    private readonly Button _textColorButton;
    private readonly Polyline _inkPreview;
    private readonly ColorPicker _textColorPicker;

    private List<Point>? _currentStroke;
    private DragMode _dragMode;
    private Point _dragStart;
    private Rect _dragStartBounds;
    private List<PointOverlay>? _dragStartInkPoints;
    private bool _dragPushedUndo;
    private bool _isErasing;
    private bool _erasePushedUndo;
    private SelectionKind _selectionKind;
    private string? _selectedId;
    private bool _isRendering;
    private bool _isSyncingColorPicker;
    private ReaderEditTool _activeTool = ReaderEditTool.Select;

    private static readonly InputCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static readonly InputCursor DrawCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
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

        _inkPreview = new Polyline
        {
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Visibility = Visibility.Collapsed
        };

        _textColorPicker = new ColorPicker
        {
            Color = Colors.Black,
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            Width = 280
        };
        _textColorPicker.ColorChanged += TextColorPicker_ColorChanged;

        _textColorButton = CreateToolbarButton(string.Empty);
        _textColorButton.Content = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Colors.Black)
        };
        _textColorButton.Flyout = new Flyout { Content = _textColorPicker };

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
                    CreateToolbarButton("A-", (_, _) => ScaleSelectedFontSize(-2)),
                    CreateToolbarButton("A+", (_, _) => ScaleSelectedFontSize(2)),
                    CreateToolbarButton("B", (_, _) => ToggleSelectedTextBold()),
                    CreateToolbarButton("I", (_, _) => ToggleSelectedTextItalic()),
                    _textColorButton
                }
            }
        };

        Children.Add(_inkPreview);
        Children.Add(_selectionAdorner);
        Children.Add(_resizeHandle);
        Children.Add(_textToolbar);
        PointerPressed += Surface_PointerPressed;
        PointerMoved += Surface_PointerMoved;
        PointerReleased += Surface_PointerReleased;
        PointerCaptureLost += Surface_PointerCaptureLost;
        DoubleTapped += Surface_DoubleTapped;
        IsDoubleTapEnabled = true;
        KeyDown += Surface_KeyDown;
        ApplyToolCursor();
    }

    public PageOverlayState Overlay { get; private set; } = new();

    public ReaderEditTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == value)
            {
                return;
            }

            _activeTool = value;
            CancelActiveGesture();
            if (value is ReaderEditTool.Ink or ReaderEditTool.Eraser)
            {
                ClearSelection();
            }

            ApplyToolInteraction();
            ApplyToolCursor();

            // Pull focus off any text box so empty ones get pruned on the way out.
            if (value != ReaderEditTool.Text)
            {
                Focus(FocusState.Programmatic);
            }
        }
    }

    public double DisplayScale { get; private set; } = 1.0;

    public string InkColorHex { get; set; } = "#000000";

    public double InkThickness { get; set; } = 2;

    public event EventHandler<PageOverlayState>? OverlayChanged;

    public event EventHandler<ReaderEditTool>? ActiveToolChangeRequested;

    public void LoadOverlay(PageOverlayState overlay, double displayScale, double width, double height)
    {
        CancelActiveGesture();
        Overlay = CloneOverlay(overlay);
        DisplayScale = displayScale <= 0 ? 1.0 : displayScale;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        _undoStack.Clear();
        ClearSelection();
        RenderOverlay();
    }

    /// <summary>Flushes in-progress text edits into the overlay model and drops empty text boxes.</summary>
    public void CommitActiveEdits()
    {
        var changed = PersistTextBoxes();

        if (PruneEmptyTextItems())
        {
            changed = true;
            RenderOverlay();
        }

        if (changed)
        {
            NotifyOverlayChanged();
        }
    }

    public void PlaceText() =>
        PlaceTextAt(new Point(PageWidth / 2 - 80, PageHeight / 2 - 20));

    public void PlaceSignature(string imageBase64, double aspectRatio = 2.0)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return;
        }

        if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
        {
            aspectRatio = 2.0;
        }

        var width = Math.Clamp(PageWidth / 3, 90, 260);
        var height = width / aspectRatio;
        if (height > PageHeight / 3)
        {
            height = PageHeight / 3;
            width = height * aspectRatio;
        }

        PushUndo();
        var signature = new SignatureOverlay
        {
            X = Math.Max(0, (PageWidth - width) / 2),
            Y = Math.Max(0, (PageHeight - height) / 2),
            ImageBase64 = imageBase64,
            Width = width,
            Height = height
        };

        Overlay.Signatures.Add(signature);
        RenderOverlay();
        SelectItem(SelectionKind.Signature, signature.Id);
        NotifyOverlayChanged();

        if (ActiveTool != ReaderEditTool.Select)
        {
            ActiveToolChangeRequested?.Invoke(this, ReaderEditTool.Select);
        }
    }

    public void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        CancelActiveGesture();
        Overlay = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        ClearSelection();
        RenderOverlay();
        NotifyOverlayChanged();
    }

    public void DeleteSelection()
    {
        if (_selectionKind == SelectionKind.None || _selectedId is null)
        {
            return;
        }

        PushUndo();
        switch (_selectionKind)
        {
            case SelectionKind.Text:
                Overlay.TextItems.RemoveAll(item => item.Id == _selectedId);
                break;
            case SelectionKind.Signature:
                Overlay.Signatures.RemoveAll(item => item.Id == _selectedId);
                break;
            case SelectionKind.Ink:
                Overlay.InkStrokes.RemoveAll(item => item.Id == _selectedId);
                break;
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

    private double PageWidth => DisplayScale > 0 ? Width / DisplayScale : Width;

    private double PageHeight => DisplayScale > 0 ? Height / DisplayScale : Height;

    // ═══════════ Rendering ═══════════

    private void RenderOverlay()
    {
        _isRendering = true;
        try
        {
            Children.Clear();

            foreach (var stroke in Overlay.InkStrokes)
            {
                var polyline = CreatePolyline(
                    stroke.Points.Select(ToCanvasPoint),
                    stroke.ColorHex,
                    stroke.Thickness * DisplayScale);
                polyline.Tag = new OverlayTag(InkTagKind, stroke.Id);
                Children.Add(polyline);
            }

            foreach (var text in Overlay.TextItems)
            {
                Children.Add(CreateTextBox(text));
            }

            foreach (var signature in Overlay.Signatures)
            {
                if (TryCreateSignatureImage(signature, out var image))
                {
                    image.Width = Math.Max(8, signature.Width * DisplayScale);
                    image.Height = Math.Max(8, signature.Height * DisplayScale);
                    image.Tag = new OverlayTag(SignatureTagKind, signature.Id);
                    SetLeft(image, signature.X * DisplayScale);
                    SetTop(image, signature.Y * DisplayScale);
                    Children.Add(image);
                }
            }

            Children.Add(_inkPreview);
            Children.Add(_selectionAdorner);
            Children.Add(_resizeHandle);
            Children.Add(_textToolbar);
            ApplyToolInteraction();
            RestoreSelectionAdorner();
        }
        finally
        {
            _isRendering = false;
        }
    }

    private TextBox CreateTextBox(TextOverlay text)
    {
        var box = new TextBox
        {
            Text = text.Text,
            PlaceholderText = "Type here",
            FontSize = Math.Max(6, text.FontSize * DisplayScale),
            Width = Math.Max(24, text.Width * DisplayScale),
            Height = Math.Max(16, text.Height * DisplayScale),
            Foreground = ColorBrushFromHex(text.ColorHex),
            FontWeight = text.IsBold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = text.IsItalic ? TextFontStyle.Italic : TextFontStyle.Normal,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Tag = new OverlayTag(TextTagKind, text.Id)
        };

        ApplyTextBoxChrome(box, text.ColorHex);
        box.TextChanged += TextBox_TextChanged;
        box.LostFocus += TextBox_LostFocus;
        box.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(Selectable_PointerPressed),
            handledEventsToo: true);
        SetLeft(box, text.X * DisplayScale);
        SetTop(box, text.Y * DisplayScale);
        return box;
    }

    /// <summary>
    /// Only the text tool needs live text boxes. Every other tool routes pointer input through the
    /// canvas so drawing, erasing and dragging behave the same over ink, text and signatures.
    /// </summary>
    private void ApplyToolInteraction()
    {
        var textEditable = ActiveTool == ReaderEditTool.Text;

        foreach (var child in Children.OfType<FrameworkElement>())
        {
            if (child.Tag is OverlayTag tag)
            {
                child.IsHitTestVisible = textEditable && tag.Kind == TextTagKind;
            }
        }
    }

    private void ApplyToolCursor() =>
        ProtectedCursor = ActiveTool switch
        {
            ReaderEditTool.Ink or ReaderEditTool.Eraser => DrawCursor,
            ReaderEditTool.Text => TextCursor,
            _ => ArrowCursor
        };

    // ═══════════ Canvas pointer handling ═══════════

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;

        switch (ActiveTool)
        {
            case ReaderEditTool.Ink:
                Focus(FocusState.Programmatic);
                _currentStroke = [point];
                _inkPreview.Stroke = ColorBrushFromHex(InkColorHex);
                _inkPreview.StrokeThickness = Math.Max(1, InkThickness * DisplayScale);
                _inkPreview.Points = BuildPointCollection(_currentStroke);
                _inkPreview.Visibility = Visibility.Visible;
                CapturePointer(e.Pointer);
                e.Handled = true;
                return;

            case ReaderEditTool.Eraser:
                Focus(FocusState.Programmatic);
                _isErasing = true;
                _erasePushedUndo = false;
                CapturePointer(e.Pointer);
                TryEraseAt(point);
                e.Handled = true;
                return;

            case ReaderEditTool.Text:
                // Existing text boxes handle their own clicks; an empty spot creates a new one.
                if (ReferenceEquals(e.OriginalSource, this))
                {
                    PlaceTextAt(ToPagePosition(point));
                    e.Handled = true;
                }

                return;
        }

        Focus(FocusState.Programmatic);

        if (HitTest(ToPagePosition(point)) is not { } hit)
        {
            ClearSelection();
            return;
        }

        SelectItem(hit.Kind, hit.Id);
        BeginDrag(DragMode.Move, point);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Surface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ActiveTool != ReaderEditTool.Select)
        {
            return;
        }

        var pagePoint = ToPagePosition(e.GetPosition(this));
        if (HitTest(pagePoint) is not { Kind: SelectionKind.Text } hit)
        {
            return;
        }

        _dragMode = DragMode.None;
        SelectItem(SelectionKind.Text, hit.Id);
        ActiveToolChangeRequested?.Invoke(this, ReaderEditTool.Text);

        if (TryGetSelectedElement() is TextBox box)
        {
            box.Focus(FocusState.Programmatic);
        }

        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;

        if (_isErasing)
        {
            TryEraseAt(point);
            e.Handled = true;
            return;
        }

        if (_currentStroke is not null)
        {
            var last = _currentStroke[^1];
            if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) >= 1.0)
            {
                _currentStroke.Add(point);
                _inkPreview.Points.Add(point);
            }

            e.Handled = true;
            return;
        }

        if (_dragMode != DragMode.None)
        {
            UpdateDrag(point);
            e.Handled = true;
        }
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isErasing)
        {
            ReleasePointerCapture(e.Pointer);
            _isErasing = false;
            if (_erasePushedUndo)
            {
                NotifyOverlayChanged();
            }

            _erasePushedUndo = false;
            e.Handled = true;
            return;
        }

        if (_currentStroke is not null)
        {
            ReleasePointerCapture(e.Pointer);
            CommitInkStroke();
            e.Handled = true;
            return;
        }

        if (_dragMode != DragMode.None)
        {
            ReleasePointerCapture(e.Pointer);
            EndDrag();
            e.Handled = true;
        }
    }

    private void Surface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_currentStroke is not null)
        {
            CommitInkStroke();
            return;
        }

        if (_isErasing)
        {
            _isErasing = false;
            if (_erasePushedUndo)
            {
                NotifyOverlayChanged();
            }

            _erasePushedUndo = false;
            return;
        }

        if (_dragMode != DragMode.None)
        {
            EndDrag();
        }
    }

    private void CommitInkStroke()
    {
        var points = _currentStroke;
        _currentStroke = null;
        _inkPreview.Visibility = Visibility.Collapsed;
        _inkPreview.Points = new PointCollection();

        if (points is null || points.Count < 2)
        {
            return;
        }

        PushUndo();
        Overlay.InkStrokes.Add(new InkStrokeOverlay
        {
            ColorHex = InkColorHex,
            Thickness = InkThickness,
            Points = points.Select(ToPagePoint).ToList()
        });

        RenderOverlay();
        NotifyOverlayChanged();
    }

    private void CancelActiveGesture()
    {
        _currentStroke = null;
        _inkPreview.Visibility = Visibility.Collapsed;
        _inkPreview.Points = new PointCollection();
        _isErasing = false;
        _erasePushedUndo = false;
        _dragMode = DragMode.None;
        _dragPushedUndo = false;
        _dragStartInkPoints = null;
    }

    // ═══════════ Selection and dragging ═══════════

    private void Selectable_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only reachable for text boxes while the text tool is active: select without stealing the caret.
        if (sender is FrameworkElement { Tag: OverlayTag tag } && tag.Kind == TextTagKind)
        {
            SelectItem(SelectionKind.Text, tag.Id);
        }
    }

    /// <summary>Topmost-first hit test against the overlay model, in page units.</summary>
    private (SelectionKind Kind, string Id)? HitTest(Point pagePoint)
    {
        for (var index = Overlay.Signatures.Count - 1; index >= 0; index--)
        {
            var item = Overlay.Signatures[index];
            if (Contains(new Rect(item.X, item.Y, item.Width, item.Height), pagePoint))
            {
                return (SelectionKind.Signature, item.Id);
            }
        }

        for (var index = Overlay.TextItems.Count - 1; index >= 0; index--)
        {
            var item = Overlay.TextItems[index];
            if (Contains(new Rect(item.X, item.Y, item.Width, item.Height), pagePoint))
            {
                return (SelectionKind.Text, item.Id);
            }
        }

        for (var index = Overlay.InkStrokes.Count - 1; index >= 0; index--)
        {
            var stroke = Overlay.InkStrokes[index];
            var tolerance = Math.Max(4, stroke.Thickness / 2 + 3);
            if (IsPointNearStroke(pagePoint, stroke.Points, tolerance))
            {
                return (SelectionKind.Ink, stroke.Id);
            }
        }

        return null;
    }

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_selectionKind is not (SelectionKind.Text or SelectionKind.Signature))
        {
            return;
        }

        BeginDrag(DragMode.Resize, e.GetCurrentPoint(this).Position);
        _resizeHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.Resize)
        {
            return;
        }

        UpdateDrag(e.GetCurrentPoint(this).Position);
        e.Handled = true;
    }

    private void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.Resize)
        {
            return;
        }

        _resizeHandle.ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void BeginDrag(DragMode mode, Point canvasPoint)
    {
        if (GetSelectionBounds() is not { } bounds)
        {
            return;
        }

        _dragMode = mode;
        _dragStart = canvasPoint;
        _dragStartBounds = bounds;
        _dragPushedUndo = false;
        _dragStartInkPoints = _selectionKind == SelectionKind.Ink
            ? FindInk(_selectedId)?.Points.Select(point => new PointOverlay { X = point.X, Y = point.Y }).ToList()
            : null;
    }

    private void UpdateDrag(Point canvasPoint)
    {
        var deltaX = (canvasPoint.X - _dragStart.X) / DisplayScale;
        var deltaY = (canvasPoint.Y - _dragStart.Y) / DisplayScale;

        if (!_dragPushedUndo)
        {
            if (Math.Abs(deltaX) * DisplayScale < 2 && Math.Abs(deltaY) * DisplayScale < 2)
            {
                return;
            }

            PushUndo();
            _dragPushedUndo = true;
        }

        if (_dragMode == DragMode.Move)
        {
            var x = Math.Max(0, _dragStartBounds.X + deltaX);
            var y = Math.Max(0, _dragStartBounds.Y + deltaY);
            MoveSelection(x - _dragStartBounds.X, y - _dragStartBounds.Y);
        }
        else if (_dragMode == DragMode.Resize)
        {
            ResizeSelection(
                Math.Max(12, _dragStartBounds.Width + deltaX),
                Math.Max(10, _dragStartBounds.Height + deltaY));
        }

        SyncSelectedElement();
        RestoreSelectionAdorner();
    }

    private void EndDrag()
    {
        var changed = _dragPushedUndo;
        _dragMode = DragMode.None;
        _dragPushedUndo = false;
        _dragStartInkPoints = null;

        if (changed)
        {
            NotifyOverlayChanged();
        }
    }

    private void MoveSelection(double offsetX, double offsetY)
    {
        switch (_selectionKind)
        {
            case SelectionKind.Text when FindText(_selectedId) is { } text:
                text.X = _dragStartBounds.X + offsetX;
                text.Y = _dragStartBounds.Y + offsetY;
                break;

            case SelectionKind.Signature when FindSignature(_selectedId) is { } signature:
                signature.X = _dragStartBounds.X + offsetX;
                signature.Y = _dragStartBounds.Y + offsetY;
                break;

            case SelectionKind.Ink when FindInk(_selectedId) is { } ink && _dragStartInkPoints is { } origin:
                for (var index = 0; index < ink.Points.Count && index < origin.Count; index++)
                {
                    ink.Points[index].X = origin[index].X + offsetX;
                    ink.Points[index].Y = origin[index].Y + offsetY;
                }

                break;
        }
    }

    private void ResizeSelection(double width, double height)
    {
        switch (_selectionKind)
        {
            case SelectionKind.Text when FindText(_selectedId) is { } text:
                text.Width = width;
                text.Height = height;
                break;

            case SelectionKind.Signature when FindSignature(_selectedId) is { } signature:
                signature.Width = width;
                signature.Height = height;
                break;
        }
    }

    /// <summary>Pushes the current model geometry back onto the live visual without a full re-render.</summary>
    private void SyncSelectedElement()
    {
        if (TryGetSelectedElement() is not { } element)
        {
            return;
        }

        switch (_selectionKind)
        {
            case SelectionKind.Text when FindText(_selectedId) is { } text:
                SetLeft(element, text.X * DisplayScale);
                SetTop(element, text.Y * DisplayScale);
                element.Width = Math.Max(24, text.Width * DisplayScale);
                element.Height = Math.Max(16, text.Height * DisplayScale);
                break;

            case SelectionKind.Signature when FindSignature(_selectedId) is { } signature:
                SetLeft(element, signature.X * DisplayScale);
                SetTop(element, signature.Y * DisplayScale);
                element.Width = Math.Max(8, signature.Width * DisplayScale);
                element.Height = Math.Max(8, signature.Height * DisplayScale);
                break;

            case SelectionKind.Ink when element is Polyline polyline && FindInk(_selectedId) is { } ink:
                polyline.Points = BuildPointCollection(ink.Points.Select(ToCanvasPoint));
                break;
        }
    }

    private void SelectItem(SelectionKind kind, string id)
    {
        _selectionKind = kind;
        _selectedId = id;
        RestoreSelectionAdorner();
    }

    private void RestoreSelectionAdorner()
    {
        if (GetSelectionBounds() is not { } bounds)
        {
            _selectionAdorner.Visibility = Visibility.Collapsed;
            _resizeHandle.Visibility = Visibility.Collapsed;
            _textToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        var left = bounds.X * DisplayScale;
        var top = bounds.Y * DisplayScale;
        var width = Math.Max(4, bounds.Width * DisplayScale);
        var height = Math.Max(4, bounds.Height * DisplayScale);

        _selectionAdorner.Width = width;
        _selectionAdorner.Height = height;
        SetLeft(_selectionAdorner, left);
        SetTop(_selectionAdorner, top);
        _selectionAdorner.Visibility = Visibility.Visible;

        _resizeHandle.Visibility = _selectionKind is SelectionKind.Text or SelectionKind.Signature
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetLeft(_resizeHandle, left + width - _resizeHandle.Width / 2);
        SetTop(_resizeHandle, top + height - _resizeHandle.Height / 2);

        if (_selectionKind == SelectionKind.Text)
        {
            UpdateTextToolbar(left, top, width);
        }
        else
        {
            _textToolbar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Selection bounds in page units, derived from the model so they never depend on layout timing.</summary>
    private Rect? GetSelectionBounds()
    {
        switch (_selectionKind)
        {
            case SelectionKind.Text when FindText(_selectedId) is { } text:
                return new Rect(text.X, text.Y, Math.Max(4, text.Width), Math.Max(4, text.Height));

            case SelectionKind.Signature when FindSignature(_selectedId) is { } signature:
                return new Rect(signature.X, signature.Y, Math.Max(4, signature.Width), Math.Max(4, signature.Height));

            case SelectionKind.Ink when FindInk(_selectedId) is { } ink && ink.Points.Count > 0:
                return GetInkBounds(ink);

            default:
                return null;
        }
    }

    private static Rect GetInkBounds(InkStrokeOverlay ink)
    {
        var minX = ink.Points.Min(point => point.X);
        var minY = ink.Points.Min(point => point.Y);
        var maxX = ink.Points.Max(point => point.X);
        var maxY = ink.Points.Max(point => point.Y);
        var padding = Math.Max(2, ink.Thickness);
        return new Rect(
            minX - padding,
            minY - padding,
            Math.Max(4, maxX - minX + padding * 2),
            Math.Max(4, maxY - minY + padding * 2));
    }

    private FrameworkElement? TryGetSelectedElement()
    {
        if (_selectionKind == SelectionKind.None || _selectedId is null)
        {
            return null;
        }

        var kind = _selectionKind switch
        {
            SelectionKind.Text => TextTagKind,
            SelectionKind.Signature => SignatureTagKind,
            _ => InkTagKind
        };

        return Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(element => element.Tag is OverlayTag tag && tag.Kind == kind && tag.Id == _selectedId);
    }

    private TextOverlay? FindText(string? id) =>
        id is null ? null : Overlay.TextItems.FirstOrDefault(item => item.Id == id);

    private SignatureOverlay? FindSignature(string? id) =>
        id is null ? null : Overlay.Signatures.FirstOrDefault(item => item.Id == id);

    private InkStrokeOverlay? FindInk(string? id) =>
        id is null ? null : Overlay.InkStrokes.FirstOrDefault(item => item.Id == id);

    // ═══════════ Text ═══════════

    private void PlaceTextAt(Point pagePoint)
    {
        PushUndo();
        var text = new TextOverlay
        {
            X = Math.Max(0, pagePoint.X),
            Y = Math.Max(0, pagePoint.Y),
            Text = string.Empty,
            Width = 160,
            Height = 32
        };

        Overlay.TextItems.Add(text);
        RenderOverlay();
        SelectItem(SelectionKind.Text, text.Id);

        if (TryGetSelectedElement() is TextBox box)
        {
            box.Focus(FocusState.Programmatic);
        }
    }

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRendering || sender is not TextBox box || box.Tag is not OverlayTag tag || tag.Kind != TextTagKind)
        {
            return;
        }

        if (FindText(tag.Id) is not { } text)
        {
            return;
        }

        text.Text = box.Text;
        NotifyOverlayChanged();
    }

    private void TextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRendering || sender is not TextBox box || box.Tag is not OverlayTag tag || tag.Kind != TextTagKind)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(box.Text))
        {
            return;
        }

        Overlay.TextItems.RemoveAll(item => item.Id == tag.Id);
        if (_selectedId == tag.Id)
        {
            ClearSelection();
        }

        RenderOverlay();
        NotifyOverlayChanged();
    }

    private bool PersistTextBoxes()
    {
        var changed = false;

        foreach (var box in Children.OfType<TextBox>())
        {
            if (box.Tag is not OverlayTag tag || tag.Kind != TextTagKind || FindText(tag.Id) is not { } text)
            {
                continue;
            }

            if (!string.Equals(text.Text, box.Text, StringComparison.Ordinal))
            {
                text.Text = box.Text;
                changed = true;
            }
        }

        return changed;
    }

    private bool PruneEmptyTextItems() =>
        Overlay.TextItems.RemoveAll(item => string.IsNullOrWhiteSpace(item.Text)) > 0;

    private void UpdateTextToolbar(double selectedLeft, double selectedTop, double selectedWidth)
    {
        if (FindText(_selectedId) is not { } text)
        {
            _textToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        if (_textColorButton.Content is Border dot)
        {
            dot.Background = ColorBrushFromHex(text.ColorHex);
        }

        _isSyncingColorPicker = true;
        _textColorPicker.Color = ParseColor(text.ColorHex);
        _isSyncingColorPicker = false;

        _textToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = _textToolbar.DesiredSize.Width;
        var left = Math.Max(0, selectedLeft + selectedWidth / 2 - toolbarWidth / 2);
        var top = selectedTop - 40;
        if (top < 0)
        {
            top = selectedTop + 8;
        }

        SetLeft(_textToolbar, left);
        SetTop(_textToolbar, top);
        _textToolbar.Visibility = Visibility.Visible;
    }

    private void ToggleSelectedTextBold()
    {
        if (FindText(_selectedId) is not { } text)
        {
            return;
        }

        PushUndo();
        text.IsBold = !text.IsBold;
        if (TryGetSelectedElement() is TextBox box)
        {
            box.FontWeight = text.IsBold ? FontWeights.Bold : FontWeights.Normal;
        }

        NotifyOverlayChanged();
    }

    private void ToggleSelectedTextItalic()
    {
        if (FindText(_selectedId) is not { } text)
        {
            return;
        }

        PushUndo();
        text.IsItalic = !text.IsItalic;
        if (TryGetSelectedElement() is TextBox box)
        {
            box.FontStyle = text.IsItalic ? TextFontStyle.Italic : TextFontStyle.Normal;
        }

        NotifyOverlayChanged();
    }

    private void ScaleSelectedFontSize(double delta)
    {
        if (FindText(_selectedId) is not { } text)
        {
            return;
        }

        var updated = Math.Clamp(text.FontSize + delta, 6, 96);
        if (Math.Abs(updated - text.FontSize) < 0.01)
        {
            return;
        }

        PushUndo();
        text.FontSize = updated;
        text.Height = Math.Max(text.Height, updated * 1.6);
        if (TryGetSelectedElement() is TextBox box)
        {
            box.FontSize = Math.Max(6, updated * DisplayScale);
            box.Height = Math.Max(16, text.Height * DisplayScale);
        }

        RestoreSelectionAdorner();
        NotifyOverlayChanged();
    }

    private void TextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_isSyncingColorPicker || FindText(_selectedId) is not { } text)
        {
            return;
        }

        var colorHex = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        if (string.Equals(colorHex, text.ColorHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PushUndo();
        text.ColorHex = colorHex;
        if (TryGetSelectedElement() is TextBox box)
        {
            box.Foreground = new SolidColorBrush(args.NewColor);
            ApplyTextBoxChrome(box, colorHex);
        }

        if (_textColorButton.Content is Border dot)
        {
            dot.Background = new SolidColorBrush(args.NewColor);
        }

        NotifyOverlayChanged();
    }

    private static void ApplyTextBoxChrome(TextBox box, string colorHex)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var foreground = ColorBrushFromHex(colorHex);
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundPointerOver"] = transparent;
        box.Resources["TextControlBackgroundFocused"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlForeground"] = foreground;
        box.Resources["TextControlForegroundPointerOver"] = foreground;
        box.Resources["TextControlForegroundFocused"] = foreground;
        box.Resources["TextControlForegroundDisabled"] = foreground;
        box.Resources["TextControlBorderBrush"] = transparent;
        box.Resources["TextControlBorderBrushPointerOver"] = transparent;
        box.Resources["TextControlBorderBrushFocused"] = transparent;
        box.Resources["TextControlBorderBrushDisabled"] = transparent;
        box.Resources["TextControlPlaceholderForeground"] = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 128, 128, 128));
    }

    private void Surface_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete || e.Key == Windows.System.VirtualKey.Back)
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

    // ═══════════ Eraser ═══════════

    private void TryEraseAt(Point canvasPoint)
    {
        if (HitTest(ToPagePosition(canvasPoint)) is not { } hit)
        {
            return;
        }

        EnsureEraseUndo();
        switch (hit.Kind)
        {
            case SelectionKind.Text:
                Overlay.TextItems.RemoveAll(item => item.Id == hit.Id);
                break;
            case SelectionKind.Signature:
                Overlay.Signatures.RemoveAll(item => item.Id == hit.Id);
                break;
            case SelectionKind.Ink:
                Overlay.InkStrokes.RemoveAll(item => item.Id == hit.Id);
                break;
        }

        ClearSelection();
        RenderOverlay();

        if (!_isErasing)
        {
            NotifyOverlayChanged();
        }
    }

    private void EnsureEraseUndo()
    {
        if (_isErasing && _erasePushedUndo)
        {
            return;
        }

        PushUndo();
        _erasePushedUndo = true;
    }

    private static bool Contains(Rect rect, Point point) =>
        point.X >= rect.X && point.X <= rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;

    private static bool IsPointNearStroke(Point point, IReadOnlyList<PointOverlay> points, double tolerance)
    {
        if (points.Count == 0)
        {
            return false;
        }

        if (points.Count == 1)
        {
            return Distance(point, new Point(points[0].X, points[0].Y)) <= tolerance;
        }

        for (var i = 1; i < points.Count; i++)
        {
            var start = new Point(points[i - 1].X, points[i - 1].Y);
            var end = new Point(points[i].X, points[i].Y);
            if (DistanceToSegment(point, start, end) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
        {
            return Distance(point, start);
        }

        var t = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Distance(point, new Point(start.X + t * dx, start.Y + t * dy));
    }

    // ═══════════ Helpers ═══════════

    private Point ToPagePosition(Point canvasPoint) =>
        new(canvasPoint.X / DisplayScale, canvasPoint.Y / DisplayScale);

    private Point ToCanvasPoint(PointOverlay point) =>
        new(point.X * DisplayScale, point.Y * DisplayScale);

    private PointOverlay ToPagePoint(Point point) =>
        new() { X = point.X / DisplayScale, Y = point.Y / DisplayScale };

    private static PointCollection BuildPointCollection(IEnumerable<Point> points)
    {
        var collection = new PointCollection();
        foreach (var point in points)
        {
            collection.Add(point);
        }

        return collection;
    }

    private static Polyline CreatePolyline(IEnumerable<Point> points, string colorHex, double thickness) =>
        new()
        {
            Stroke = ColorBrushFromHex(colorHex),
            StrokeThickness = Math.Max(1, thickness),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Points = BuildPointCollection(points)
        };

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

    private void NotifyOverlayChanged() =>
        OverlayChanged?.Invoke(this, CloneOverlay(Overlay));

    private static PageOverlayState CloneOverlay(PageOverlayState source) =>
        new()
        {
            InkStrokes = source.InkStrokes
                .Select(stroke => new InkStrokeOverlay
                {
                    Id = stroke.Id,
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

    private static Button CreateToolbarButton(string text, RoutedEventHandler? click = null)
    {
        var button = new Button
        {
            Content = text,
            Width = 30,
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
