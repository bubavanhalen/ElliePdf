using ElliePdf.Models;
using ElliePdf.Services;
using ElliePdf.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Storage.Streams;
using PointerDeviceType = Microsoft.UI.Input.PointerDeviceType;
using TextFontStyle = Windows.UI.Text.FontStyle;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using XamlShape = Microsoft.UI.Xaml.Shapes.Shape;

namespace ElliePdf.Controls;

public sealed partial class PdfEditSurface : Canvas
{
    private const string InkTagKind = "ink";
    private const string TextTagKind = "text";
    private const string SignatureTagKind = "signature";
    private const string ShapeTagKind = "shape";

    /// <summary>Metrically compatible with the PDF standard Helvetica used when embedding text.</summary>
    private static readonly FontFamily OverlayFontFamily = new("Arial");

    /// <summary>Kept in sync with <c>PdfOverlayWriter.TextPadding</c>, in page units.</summary>
    private const double TextPadding = 2;

    /// <summary>
    /// Alpha used for a shape's interior. Matches <c>PdfOverlayWriter.ShapeFillAlpha</c> so a
    /// filled shape looks the same on screen as it does once saved.
    /// </summary>
    private const byte ShapeFillAlpha = 70;

    private enum SelectionKind
    {
        None,
        Text,
        Signature,
        Ink,
        Shape
    }

    private enum DragMode
    {
        None,
        Move,
        Resize
    }

    private sealed record OverlayTag(string Kind, string Id);

    private readonly Border _selectionAdorner;
    private readonly Border _resizeHandle;
    private readonly Border _textToolbar;
    private readonly Border _styleToolbar;
    private readonly Button _textColorButton;
    private readonly Button _styleColorButton;
    private readonly ColorPicker _textColorPicker;
    private readonly ColorPicker _styleColorPicker;
    private readonly XamlPath _inkPreview;
    private readonly Polyline _inkPreviewLine;
    private readonly Canvas _shapePreview;
    private readonly Ellipse _eraserCursor;

    private List<PointOverlay>? _currentStroke;
    private Point? _shapeStart;
    private DragMode _dragMode;
    private Point _dragStart;
    private Rect _dragStartBounds;
    private Rect _dragStartGeometry;
    private List<PointOverlay>? _dragStartInkPoints;
    private (PointOverlay Start, PointOverlay End)? _dragStartShape;
    private bool _dragPushedUndo;
    private bool _isErasing;
    private bool _erasePushedUndo;
    private SelectionKind _selectionKind;
    private string? _selectedId;
    private bool _isRendering;
    private bool _isSyncingColorPicker;
    private bool _colorPushedUndo;
    private ReaderEditTool _activeTool = ReaderEditTool.Select;
    private uint? _activePointerId;
    private PointerDeviceType _activePointerDevice;
    private bool _penInRange;

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

        _inkPreviewLine = new Polyline
        {
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Visibility = Visibility.Collapsed
        };

        _inkPreview = new XamlPath
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        _shapePreview = new Canvas { IsHitTestVisible = false };

        _eraserCursor = new Ellipse
        {
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 90, 90, 90)),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 140, 140, 140)),
            Visibility = Visibility.Collapsed
        };

        _textColorPicker = CreateColorPicker(TextColorPicker_ColorChanged);
        _textColorButton = CreateToolbarButton(string.Empty);
        _textColorButton.Content = CreateColorDot();
        _textColorButton.Flyout = CreateColorFlyout(_textColorPicker);

        _textToolbar = CreateToolbarShell(new StackPanel
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
        });

        _styleColorPicker = CreateColorPicker(StyleColorPicker_ColorChanged);
        _styleColorButton = CreateToolbarButton(string.Empty);
        _styleColorButton.Content = CreateColorDot();
        _styleColorButton.Flyout = CreateColorFlyout(_styleColorPicker);

        _styleToolbar = CreateToolbarShell(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                _styleColorButton,
                CreateToolbarButton("−", (_, _) => ScaleSelectedThickness(-1)),
                CreateToolbarButton("+", (_, _) => ScaleSelectedThickness(1)),
                CreateToolbarButton("▣", (_, _) => ToggleSelectedFill())
            }
        });

        AddOverlayChrome();
        PointerPressed += Surface_PointerPressed;
        PointerMoved += Surface_PointerMoved;
        PointerReleased += Surface_PointerReleased;
        PointerCaptureLost += Surface_PointerCaptureLost;
        PointerExited += Surface_PointerExited;
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

            if (value is not ReaderEditTool.Select and not ReaderEditTool.Text)
            {
                ClearSelection();
            }

            ApplyToolInteraction();
            ApplyToolCursor();
            _eraserCursor.Visibility = Visibility.Collapsed;

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

    /// <summary>Eraser radius in page units.</summary>
    public double EraserRadius { get; set; } = 10;

    /// <summary>When true the eraser cuts strokes apart; otherwise it removes whole items.</summary>
    public bool ErasePartially { get; set; } = true;

    public event EventHandler<PageOverlayState>? OverlayChanged;

    public event EventHandler<ReaderEditTool>? ActiveToolChangeRequested;

    /// <summary>Raised with a copy of the overlay immediately before it is modified.</summary>
    public event EventHandler<PageOverlayState>? EditRecording;

    public void LoadOverlay(PageOverlayState overlay, double displayScale, double width, double height)
    {
        CancelActiveGesture();
        Overlay = OverlayHistory.Clone(overlay);
        DisplayScale = displayScale <= 0 ? 1.0 : displayScale;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
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

        RecordEdit();
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

    /// <summary>Replaces the overlay without recording history, used when undo or redo rewinds a page.</summary>
    public void ApplyHistoryState(PageOverlayState state)
    {
        CancelActiveGesture();
        Overlay = OverlayHistory.Clone(state);
        ClearSelection();
        RenderOverlay();
    }

    public void DeleteSelection()
    {
        if (_selectionKind == SelectionKind.None || _selectedId is null)
        {
            return;
        }

        RecordEdit();
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
            case SelectionKind.Shape:
                Overlay.Shapes.RemoveAll(item => item.Id == _selectedId);
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
        _styleToolbar.Visibility = Visibility.Collapsed;
    }

    private double PageWidth => DisplayScale > 0 ? Width / DisplayScale : Width;

    private double PageHeight => DisplayScale > 0 ? Height / DisplayScale : Height;

    // ═══════════ Rendering ═══════════

    private void AddOverlayChrome()
    {
        Children.Add(_inkPreviewLine);
        Children.Add(_inkPreview);
        Children.Add(_shapePreview);
        Children.Add(_eraserCursor);
        Children.Add(_selectionAdorner);
        Children.Add(_resizeHandle);
        Children.Add(_textToolbar);
        Children.Add(_styleToolbar);
    }

    private void RenderOverlay()
    {
        _isRendering = true;
        try
        {
            Children.Clear();

            foreach (var stroke in Overlay.InkStrokes)
            {
                var visual = CreateInkVisual(stroke);
                if (visual is not null)
                {
                    visual.Tag = new OverlayTag(InkTagKind, stroke.Id);
                    Children.Add(visual);
                }
            }

            foreach (var shape in Overlay.Shapes)
            {
                foreach (var visual in CreateShapeVisuals(shape))
                {
                    visual.Tag = new OverlayTag(ShapeTagKind, shape.Id);
                    Children.Add(visual);
                }
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

            AddOverlayChrome();
            ApplyToolInteraction();
            RestoreSelectionAdorner();
        }
        finally
        {
            _isRendering = false;
        }
    }

    /// <summary>
    /// Uniform-pressure strokes render as a plain polyline, which stays crisp; pressure-varying
    /// strokes become a filled ribbon so the width can taper.
    /// </summary>
    private FrameworkElement? CreateInkVisual(InkStrokeOverlay stroke)
    {
        if (stroke.Points.Count < 2)
        {
            return null;
        }

        if (InkGeometry.HasUniformPressure(stroke.Points))
        {
            var width = InkGeometry.WidthAt(stroke.Thickness, stroke.Points[0].Pressure);
            return CreatePolyline(
                stroke.Points.Select(point => ToCanvasPoint(point)),
                stroke.ColorHex,
                width * DisplayScale);
        }

        var outline = InkGeometry.BuildOutline(stroke.Points, stroke.Thickness);
        if (outline.Count < 3)
        {
            return null;
        }

        return new XamlPath
        {
            Fill = ColorBrushFromHex(stroke.ColorHex),
            Data = BuildPolygonGeometry(outline.Select(vertex => new Point(
                vertex.X * DisplayScale,
                vertex.Y * DisplayScale)))
        };
    }

    private IEnumerable<XamlShape> CreateShapeVisuals(ShapeOverlay shape)
    {
        var stroke = ColorBrushFromHex(shape.ColorHex);
        var thickness = Math.Max(1, shape.Thickness * DisplayScale);
        var fill = shape.FillColorHex is null ? null : FillBrushFromHex(shape.FillColorHex);

        switch (shape.Kind)
        {
            case ShapeKind.Rectangle:
            {
                var (left, top, width, height) = ShapeGeometry.Bounds(shape);
                var rectangle = new Rectangle
                {
                    Width = Math.Max(1, width * DisplayScale),
                    Height = Math.Max(1, height * DisplayScale),
                    Stroke = stroke,
                    StrokeThickness = thickness,
                    Fill = fill
                };
                SetLeft(rectangle, left * DisplayScale);
                SetTop(rectangle, top * DisplayScale);
                yield return rectangle;
                break;
            }

            case ShapeKind.Ellipse:
            {
                var (left, top, width, height) = ShapeGeometry.Bounds(shape);
                var ellipse = new Ellipse
                {
                    Width = Math.Max(1, width * DisplayScale),
                    Height = Math.Max(1, height * DisplayScale),
                    Stroke = stroke,
                    StrokeThickness = thickness,
                    Fill = fill
                };
                SetLeft(ellipse, left * DisplayScale);
                SetTop(ellipse, top * DisplayScale);
                yield return ellipse;
                break;
            }

            case ShapeKind.Line:
                yield return new Line
                {
                    X1 = shape.Start.X * DisplayScale,
                    Y1 = shape.Start.Y * DisplayScale,
                    X2 = shape.End.X * DisplayScale,
                    Y2 = shape.End.Y * DisplayScale,
                    Stroke = stroke,
                    StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                break;

            default:
            {
                var shaftEnd = ShapeGeometry.ArrowShaftEnd(shape);
                yield return new Line
                {
                    X1 = shape.Start.X * DisplayScale,
                    Y1 = shape.Start.Y * DisplayScale,
                    X2 = shaftEnd.X * DisplayScale,
                    Y2 = shaftEnd.Y * DisplayScale,
                    Stroke = stroke,
                    StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Round
                };

                if (ShapeGeometry.ArrowHead(shape) is { } head)
                {
                    yield return new XamlPath
                    {
                        Fill = stroke,
                        Data = BuildPolygonGeometry(head.Select(vertex => new Point(
                            vertex.X * DisplayScale,
                            vertex.Y * DisplayScale)))
                    };
                }

                break;
            }
        }
    }

    private static PathGeometry BuildPolygonGeometry(IEnumerable<Point> points)
    {
        var ordered = points.ToList();
        var figure = new PathFigure
        {
            StartPoint = ordered.Count > 0 ? ordered[0] : new Point(0, 0),
            IsClosed = true,
            IsFilled = true
        };

        var segment = new PolyLineSegment();
        for (var index = 1; index < ordered.Count; index++)
        {
            segment.Points.Add(ordered[index]);
        }

        figure.Segments.Add(segment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private TextBox CreateTextBox(TextOverlay text)
    {
        var box = new TextBox
        {
            Text = text.Text,
            PlaceholderText = "Type here",
            FontFamily = OverlayFontFamily,
            FontSize = Math.Max(6, text.FontSize * DisplayScale),
            Width = Math.Max(24, text.Width * DisplayScale),
            Height = Math.Max(16, text.Height * DisplayScale),
            Foreground = ColorBrushFromHex(text.ColorHex),
            FontWeight = text.IsBold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = text.IsItalic ? TextFontStyle.Italic : TextFontStyle.Normal,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(TextPadding * DisplayScale),
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
    /// canvas so drawing, erasing and dragging behave the same over every kind of annotation.
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
            _ when ActiveTool.IsShape() => DrawCursor,
            _ => ArrowCursor
        };

    // ═══════════ Canvas pointer handling ═══════════

    /// <summary>
    /// Rejects touch contacts while a pen is hovering or drawing, so a resting palm cannot scribble
    /// over the page, and ignores secondary contacts once a gesture is underway.
    /// </summary>
    private bool ShouldIgnore(PointerRoutedEventArgs e)
    {
        var device = e.Pointer.PointerDeviceType;

        if (_activePointerId is { } active)
        {
            return e.Pointer.PointerId != active;
        }

        return device == PointerDeviceType.Touch && _penInRange;
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ShouldIgnore(e))
        {
            e.Handled = true;
            return;
        }

        var currentPoint = e.GetCurrentPoint(this);
        var point = currentPoint.Position;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            _penInRange = true;
        }

        switch (ActiveTool)
        {
            case ReaderEditTool.Ink:
                Focus(FocusState.Programmatic);
                BeginGesture(e);
                _currentStroke = [ToPagePoint(currentPoint)];
                UpdateInkPreview();
                CapturePointer(e.Pointer);
                e.Handled = true;
                return;

            case ReaderEditTool.Eraser:
                Focus(FocusState.Programmatic);
                BeginGesture(e);
                _isErasing = true;
                _erasePushedUndo = false;
                CapturePointer(e.Pointer);
                UpdateEraserCursor(point);
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

        if (ActiveTool.IsShape())
        {
            Focus(FocusState.Programmatic);
            BeginGesture(e);
            _shapeStart = point;
            CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        Focus(FocusState.Programmatic);

        if (HitTest(ToPagePosition(point)) is not { } hit)
        {
            ClearSelection();
            return;
        }

        BeginGesture(e);
        SelectItem(hit.Kind, hit.Id);
        BeginDrag(DragMode.Move, point);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void BeginGesture(PointerRoutedEventArgs e)
    {
        _activePointerId = e.Pointer.PointerId;
        _activePointerDevice = e.Pointer.PointerDeviceType;
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
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            _penInRange = true;
        }

        var currentPoint = e.GetCurrentPoint(this);
        var point = currentPoint.Position;

        if (ActiveTool == ReaderEditTool.Eraser && _activePointerId is null)
        {
            UpdateEraserCursor(point);
        }

        if (ShouldIgnore(e))
        {
            return;
        }

        if (_isErasing)
        {
            UpdateEraserCursor(point);
            TryEraseAt(point);
            e.Handled = true;
            return;
        }

        if (_currentStroke is not null)
        {
            var sample = ToPagePoint(currentPoint);
            var last = _currentStroke[^1];

            // Skip samples that add no visible detail at the current zoom.
            if ((Math.Abs(sample.X - last.X) + Math.Abs(sample.Y - last.Y)) * DisplayScale >= 1.0)
            {
                _currentStroke.Add(sample);
                UpdateInkPreview();
            }

            e.Handled = true;
            return;
        }

        if (_shapeStart is { } start)
        {
            UpdateShapePreview(start, point);
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
        if (ShouldIgnore(e))
        {
            return;
        }

        if (_isErasing)
        {
            ReleasePointerCapture(e.Pointer);
            FinishErase();
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

        if (_shapeStart is { } start)
        {
            ReleasePointerCapture(e.Pointer);
            CommitShape(start, e.GetCurrentPoint(this).Position);
            e.Handled = true;
            return;
        }

        if (_dragMode != DragMode.None)
        {
            ReleasePointerCapture(e.Pointer);
            EndDrag();
            e.Handled = true;
            return;
        }

        _activePointerId = null;
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
            FinishErase();
            return;
        }

        if (_shapeStart is not null)
        {
            CancelActiveGesture();
            return;
        }

        if (_dragMode != DragMode.None)
        {
            EndDrag();
            return;
        }

        _activePointerId = null;
    }

    private void Surface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            _penInRange = false;
        }

        if (_activePointerId is null)
        {
            _eraserCursor.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateInkPreview()
    {
        if (_currentStroke is null || _currentStroke.Count == 0)
        {
            _inkPreview.Visibility = Visibility.Collapsed;
            _inkPreviewLine.Visibility = Visibility.Collapsed;
            return;
        }

        if (InkGeometry.HasUniformPressure(_currentStroke))
        {
            var width = InkGeometry.WidthAt(InkThickness, _currentStroke[0].Pressure);
            _inkPreviewLine.Stroke = ColorBrushFromHex(InkColorHex);
            _inkPreviewLine.StrokeThickness = Math.Max(1, width * DisplayScale);
            _inkPreviewLine.Points = BuildPointCollection(_currentStroke.Select(ToCanvasPoint));
            _inkPreviewLine.Visibility = Visibility.Visible;
            _inkPreview.Visibility = Visibility.Collapsed;
            return;
        }

        var outline = InkGeometry.BuildOutline(_currentStroke, InkThickness);
        if (outline.Count < 3)
        {
            return;
        }

        _inkPreview.Fill = ColorBrushFromHex(InkColorHex);
        _inkPreview.Data = BuildPolygonGeometry(outline.Select(vertex => new Point(
            vertex.X * DisplayScale,
            vertex.Y * DisplayScale)));
        _inkPreview.Visibility = Visibility.Visible;
        _inkPreviewLine.Visibility = Visibility.Collapsed;
    }

    private void CommitInkStroke()
    {
        var points = _currentStroke;
        _currentStroke = null;
        _activePointerId = null;
        _inkPreview.Visibility = Visibility.Collapsed;
        _inkPreviewLine.Visibility = Visibility.Collapsed;

        if (points is null || points.Count < 2)
        {
            return;
        }

        NormalizeUnsupportedPressure(points);

        RecordEdit();
        Overlay.InkStrokes.Add(new InkStrokeOverlay
        {
            ColorHex = InkColorHex,
            Thickness = InkThickness,
            Points = points
        });

        RenderOverlay();
        NotifyOverlayChanged();
    }

    private void UpdateShapePreview(Point start, Point current)
    {
        _shapePreview.Children.Clear();

        var preview = BuildShape(start, current);
        if (preview is null)
        {
            return;
        }

        foreach (var visual in CreateShapeVisuals(preview))
        {
            visual.Opacity = 0.85;
            _shapePreview.Children.Add(visual);
        }
    }

    private ShapeOverlay? BuildShape(Point start, Point end)
    {
        var from = ToPagePosition(start);
        var to = ToPagePosition(end);

        return new ShapeOverlay
        {
            Kind = ActiveTool.ToShapeKind(),
            Start = new PointOverlay { X = from.X, Y = from.Y },
            End = new PointOverlay { X = to.X, Y = to.Y },
            ColorHex = InkColorHex,
            Thickness = InkThickness
        };
    }

    private void CommitShape(Point start, Point end)
    {
        _shapeStart = null;
        _activePointerId = null;
        _shapePreview.Children.Clear();

        var shape = BuildShape(start, end);
        if (shape is null)
        {
            return;
        }

        var dx = Math.Abs(shape.End.X - shape.Start.X) * DisplayScale;
        var dy = Math.Abs(shape.End.Y - shape.Start.Y) * DisplayScale;

        // A click without a drag is not a shape.
        if (dx < 4 && dy < 4)
        {
            return;
        }

        RecordEdit();
        Overlay.Shapes.Add(shape);
        RenderOverlay();
        SelectItem(SelectionKind.Shape, shape.Id);
        NotifyOverlayChanged();

        ActiveToolChangeRequested?.Invoke(this, ReaderEditTool.Select);
    }

    private void CancelActiveGesture()
    {
        _currentStroke = null;
        _shapeStart = null;
        _activePointerId = null;
        _inkPreview.Visibility = Visibility.Collapsed;
        _inkPreviewLine.Visibility = Visibility.Collapsed;
        _shapePreview.Children.Clear();
        _isErasing = false;
        _erasePushedUndo = false;
        _dragMode = DragMode.None;
        _dragPushedUndo = false;
        _dragStartInkPoints = null;
        _dragStartShape = null;
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

        for (var index = Overlay.Shapes.Count - 1; index >= 0; index--)
        {
            var shape = Overlay.Shapes[index];
            var tolerance = HitTolerance(shape.Thickness);

            if (ShapeGeometry.DistanceTo(shape, pagePoint.X, pagePoint.Y) <= tolerance ||
                ShapeGeometry.ContainsInterior(shape, pagePoint.X, pagePoint.Y))
            {
                return (SelectionKind.Shape, shape.Id);
            }
        }

        for (var index = Overlay.InkStrokes.Count - 1; index >= 0; index--)
        {
            var stroke = Overlay.InkStrokes[index];
            if (InkGeometry.DistanceTo(stroke.Points, pagePoint.X, pagePoint.Y) <= HitTolerance(stroke.Thickness))
            {
                return (SelectionKind.Ink, stroke.Id);
            }
        }

        return null;
    }

    /// <summary>
    /// Hit tolerance in page units. The slack is defined in screen pixels and converted, so targets
    /// stay equally easy to hit at any zoom level.
    /// </summary>
    private double HitTolerance(double thickness)
    {
        const double screenSlack = 6;
        return Math.Max(thickness / 2, 1) + (screenSlack / Math.Max(0.05, DisplayScale));
    }

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_selectionKind is SelectionKind.None)
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
        if (GetSelectionBounds() is not { } bounds || GetGeometryBounds() is not { } geometry)
        {
            return;
        }

        _dragMode = mode;
        _dragStart = canvasPoint;
        _dragStartBounds = bounds;
        _dragStartGeometry = geometry;
        _dragPushedUndo = false;

        _dragStartInkPoints = _selectionKind == SelectionKind.Ink
            ? FindInk(_selectedId)?.Points
                .Select(point => new PointOverlay { X = point.X, Y = point.Y, Pressure = point.Pressure })
                .ToList()
            : null;

        _dragStartShape = _selectionKind == SelectionKind.Shape && FindShape(_selectedId) is { } shape
            ? (new PointOverlay { X = shape.Start.X, Y = shape.Start.Y },
               new PointOverlay { X = shape.End.X, Y = shape.End.Y })
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

            RecordEdit();
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
            // Resize works off the unpadded geometry, so grabbing the handle does not itself
            // rescale the item by whatever padding the selection box adds for stroke width.
            ResizeSelection(
                Math.Max(4, _dragStartGeometry.Width + deltaX),
                Math.Max(4, _dragStartGeometry.Height + deltaY));
        }

        RenderSelectionOnly();
        RestoreSelectionAdorner();
    }

    private void EndDrag()
    {
        var changed = _dragPushedUndo;
        _dragMode = DragMode.None;
        _dragPushedUndo = false;
        _dragStartInkPoints = null;
        _dragStartShape = null;
        _activePointerId = null;

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

            case SelectionKind.Shape when FindShape(_selectedId) is { } shape && _dragStartShape is { } origin:
                shape.Start.X = origin.Start.X + offsetX;
                shape.Start.Y = origin.Start.Y + offsetY;
                shape.End.X = origin.End.X + offsetX;
                shape.End.Y = origin.End.Y + offsetY;
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

            case SelectionKind.Shape when FindShape(_selectedId) is { } shape && _dragStartShape is { } origin:
            {
                // Scale about the anchor corner so the drag feels like a normal resize handle.
                var originalWidth = Math.Abs(origin.End.X - origin.Start.X);
                var originalHeight = Math.Abs(origin.End.Y - origin.Start.Y);
                var scaleX = originalWidth > 1e-6 ? width / originalWidth : 1;
                var scaleY = originalHeight > 1e-6 ? height / originalHeight : 1;

                var anchorX = Math.Min(origin.Start.X, origin.End.X);
                var anchorY = Math.Min(origin.Start.Y, origin.End.Y);

                shape.Start.X = anchorX + ((origin.Start.X - anchorX) * scaleX);
                shape.Start.Y = anchorY + ((origin.Start.Y - anchorY) * scaleY);
                shape.End.X = anchorX + ((origin.End.X - anchorX) * scaleX);
                shape.End.Y = anchorY + ((origin.End.Y - anchorY) * scaleY);
                break;
            }

            case SelectionKind.Ink when FindInk(_selectedId) is { } ink && _dragStartInkPoints is { } origin:
            {
                var bounds = InkBounds(origin);
                var scaleX = bounds.Width > 1e-6 ? width / bounds.Width : 1;
                var scaleY = bounds.Height > 1e-6 ? height / bounds.Height : 1;

                for (var index = 0; index < ink.Points.Count && index < origin.Count; index++)
                {
                    ink.Points[index].X = bounds.X + ((origin[index].X - bounds.X) * scaleX);
                    ink.Points[index].Y = bounds.Y + ((origin[index].Y - bounds.Y) * scaleY);
                }

                break;
            }
        }
    }

    /// <summary>Re-renders just the selected item during a drag, avoiding a full rebuild per frame.</summary>
    private void RenderSelectionOnly()
    {
        if (_selectedId is null)
        {
            return;
        }

        switch (_selectionKind)
        {
            case SelectionKind.Text when FindText(_selectedId) is { } text &&
                                         TryGetSelectedElement() is { } textElement:
                SetLeft(textElement, text.X * DisplayScale);
                SetTop(textElement, text.Y * DisplayScale);
                textElement.Width = Math.Max(24, text.Width * DisplayScale);
                textElement.Height = Math.Max(16, text.Height * DisplayScale);
                break;

            case SelectionKind.Signature when FindSignature(_selectedId) is { } signature &&
                                              TryGetSelectedElement() is { } signatureElement:
                SetLeft(signatureElement, signature.X * DisplayScale);
                SetTop(signatureElement, signature.Y * DisplayScale);
                signatureElement.Width = Math.Max(8, signature.Width * DisplayScale);
                signatureElement.Height = Math.Max(8, signature.Height * DisplayScale);
                break;

            default:
                // Ink and shapes can change their whole geometry, so rebuild them.
                RenderOverlay();
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
            _styleToolbar.Visibility = Visibility.Collapsed;
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

        _resizeHandle.Visibility = Visibility.Visible;
        SetLeft(_resizeHandle, left + width - (_resizeHandle.Width / 2));
        SetTop(_resizeHandle, top + height - (_resizeHandle.Height / 2));

        if (_selectionKind == SelectionKind.Text)
        {
            UpdateTextToolbar(left, top, width);
            _styleToolbar.Visibility = Visibility.Collapsed;
        }
        else if (_selectionKind is SelectionKind.Ink or SelectionKind.Shape)
        {
            UpdateStyleToolbar(left, top, width);
            _textToolbar.Visibility = Visibility.Collapsed;
        }
        else
        {
            _textToolbar.Visibility = Visibility.Collapsed;
            _styleToolbar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The item's true geometry in page units, without the stroke-width padding that
    /// <see cref="GetSelectionBounds"/> adds for the visible selection box.
    /// </summary>
    private Rect? GetGeometryBounds()
    {
        switch (_selectionKind)
        {
            case SelectionKind.Text when FindText(_selectedId) is { } text:
                return new Rect(text.X, text.Y, Math.Max(4, text.Width), Math.Max(4, text.Height));

            case SelectionKind.Signature when FindSignature(_selectedId) is { } signature:
                return new Rect(signature.X, signature.Y, Math.Max(4, signature.Width), Math.Max(4, signature.Height));

            case SelectionKind.Ink when FindInk(_selectedId) is { } ink && ink.Points.Count > 0:
                return InkBounds(ink.Points);

            case SelectionKind.Shape when FindShape(_selectedId) is { } shape:
            {
                var (left, top, width, height) = ShapeGeometry.Bounds(shape);
                return new Rect(left, top, Math.Max(1e-6, width), Math.Max(1e-6, height));
            }

            default:
                return null;
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
            {
                var bounds = InkBounds(ink.Points);
                var padding = Math.Max(2, ink.Thickness);
                return new Rect(
                    bounds.X - padding,
                    bounds.Y - padding,
                    Math.Max(4, bounds.Width + (padding * 2)),
                    Math.Max(4, bounds.Height + (padding * 2)));
            }

            case SelectionKind.Shape when FindShape(_selectedId) is { } shape:
            {
                var (left, top, width, height) = ShapeGeometry.SelectionBounds(shape);
                return new Rect(left, top, Math.Max(4, width), Math.Max(4, height));
            }

            default:
                return null;
        }
    }

    private static Rect InkBounds(IReadOnlyList<PointOverlay> points)
    {
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        return new Rect(minX, minY, Math.Max(1e-6, maxX - minX), Math.Max(1e-6, maxY - minY));
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
            SelectionKind.Shape => ShapeTagKind,
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

    private ShapeOverlay? FindShape(string? id) =>
        id is null ? null : Overlay.Shapes.FirstOrDefault(item => item.Id == id);

    // ═══════════ Text ═══════════

    private void PlaceTextAt(Point pagePoint)
    {
        RecordEdit();
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

        SetColorDot(_textColorButton, text.ColorHex);
        _isSyncingColorPicker = true;
        _textColorPicker.Color = ParseColor(text.ColorHex);
        _isSyncingColorPicker = false;

        PositionToolbar(_textToolbar, selectedLeft, selectedTop, selectedWidth);
    }

    private void UpdateStyleToolbar(double selectedLeft, double selectedTop, double selectedWidth)
    {
        var colorHex = _selectionKind switch
        {
            SelectionKind.Ink => FindInk(_selectedId)?.ColorHex,
            SelectionKind.Shape => FindShape(_selectedId)?.ColorHex,
            _ => null
        };

        if (colorHex is null)
        {
            _styleToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        SetColorDot(_styleColorButton, colorHex);
        _isSyncingColorPicker = true;
        _styleColorPicker.Color = ParseColor(colorHex);
        _isSyncingColorPicker = false;

        PositionToolbar(_styleToolbar, selectedLeft, selectedTop, selectedWidth);
    }

    private void PositionToolbar(Border toolbar, double selectedLeft, double selectedTop, double selectedWidth)
    {
        toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = toolbar.DesiredSize.Width;
        var left = Math.Max(0, selectedLeft + (selectedWidth / 2) - (toolbarWidth / 2));
        var top = selectedTop - 40;
        if (top < 0)
        {
            top = selectedTop + 8;
        }

        SetLeft(toolbar, left);
        SetTop(toolbar, top);
        toolbar.Visibility = Visibility.Visible;
    }

    private void ToggleSelectedTextBold()
    {
        if (FindText(_selectedId) is not { } text)
        {
            return;
        }

        RecordEdit();
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

        RecordEdit();
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

        RecordEdit();
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

    // ═══════════ Restyling ═══════════

    private void ScaleSelectedThickness(double delta)
    {
        switch (_selectionKind)
        {
            case SelectionKind.Ink when FindInk(_selectedId) is { } ink:
            {
                var updated = Math.Clamp(ink.Thickness + delta, 1, 40);
                if (Math.Abs(updated - ink.Thickness) < 0.01)
                {
                    return;
                }

                RecordEdit();
                ink.Thickness = updated;
                break;
            }

            case SelectionKind.Shape when FindShape(_selectedId) is { } shape:
            {
                var updated = Math.Clamp(shape.Thickness + delta, 1, 40);
                if (Math.Abs(updated - shape.Thickness) < 0.01)
                {
                    return;
                }

                RecordEdit();
                shape.Thickness = updated;
                break;
            }

            default:
                return;
        }

        RenderOverlay();
        NotifyOverlayChanged();
    }

    /// <summary>Cycles a closed shape between unfilled and filled with a translucent tint of its outline.</summary>
    private void ToggleSelectedFill()
    {
        if (FindShape(_selectedId) is not { } shape || shape.Kind is ShapeKind.Line or ShapeKind.Arrow)
        {
            return;
        }

        RecordEdit();
        shape.FillColorHex = shape.FillColorHex is null ? shape.ColorHex : null;
        RenderOverlay();
        NotifyOverlayChanged();
    }

    private void StyleColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_isSyncingColorPicker)
        {
            return;
        }

        var colorHex = ToHex(args.NewColor);

        switch (_selectionKind)
        {
            case SelectionKind.Ink when FindInk(_selectedId) is { } ink:
                if (string.Equals(colorHex, ink.ColorHex, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                RecordColorEditOnce();
                ink.ColorHex = colorHex;
                break;

            case SelectionKind.Shape when FindShape(_selectedId) is { } shape:
                if (string.Equals(colorHex, shape.ColorHex, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                RecordColorEditOnce();
                if (shape.FillColorHex is not null)
                {
                    shape.FillColorHex = colorHex;
                }

                shape.ColorHex = colorHex;
                break;

            default:
                return;
        }

        SetColorDot(_styleColorButton, colorHex);
        RenderOverlay();
        NotifyOverlayChanged();
    }

    private void TextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_isSyncingColorPicker || FindText(_selectedId) is not { } text)
        {
            return;
        }

        var colorHex = ToHex(args.NewColor);
        if (string.Equals(colorHex, text.ColorHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RecordColorEditOnce();
        text.ColorHex = colorHex;
        if (TryGetSelectedElement() is TextBox box)
        {
            box.Foreground = new SolidColorBrush(args.NewColor);
            ApplyTextBoxChrome(box, colorHex);
        }

        SetColorDot(_textColorButton, colorHex);
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

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Delete:
            case Windows.System.VirtualKey.Back:
                DeleteSelection();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Escape:
                ClearSelection();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Left:
                NudgeSelection(-1, 0);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Right:
                NudgeSelection(1, 0);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                NudgeSelection(0, -1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down:
                NudgeSelection(0, 1);
                e.Handled = true;
                break;
        }
    }

    private void NudgeSelection(double offsetX, double offsetY)
    {
        if (_selectionKind == SelectionKind.None || GetSelectionBounds() is not { } bounds)
        {
            return;
        }

        RecordEdit();
        _dragStartBounds = bounds;
        _dragStartInkPoints = FindInk(_selectedId)?.Points
            .Select(point => new PointOverlay { X = point.X, Y = point.Y, Pressure = point.Pressure })
            .ToList();
        _dragStartShape = FindShape(_selectedId) is { } shape
            ? (new PointOverlay { X = shape.Start.X, Y = shape.Start.Y },
               new PointOverlay { X = shape.End.X, Y = shape.End.Y })
            : null;

        MoveSelection(offsetX, offsetY);
        _dragStartInkPoints = null;
        _dragStartShape = null;

        RenderOverlay();
        NotifyOverlayChanged();
    }

    // ═══════════ Eraser ═══════════

    private void UpdateEraserCursor(Point canvasPoint)
    {
        var radius = EraserRadius * DisplayScale;
        _eraserCursor.Width = radius * 2;
        _eraserCursor.Height = radius * 2;
        SetLeft(_eraserCursor, canvasPoint.X - radius);
        SetTop(_eraserCursor, canvasPoint.Y - radius);
        _eraserCursor.Visibility = Visibility.Visible;
    }

    private void TryEraseAt(Point canvasPoint)
    {
        var pagePoint = ToPagePosition(canvasPoint);

        if (ErasePartially ? ErasePartial(pagePoint) : EraseWhole(pagePoint))
        {
            RenderOverlay();
        }
    }

    /// <summary>Cuts ink where the eraser touches it, and removes other annotations outright.</summary>
    private bool ErasePartial(Point pagePoint)
    {
        var changed = false;

        for (var index = Overlay.InkStrokes.Count - 1; index >= 0; index--)
        {
            var stroke = Overlay.InkStrokes[index];
            var reach = EraserRadius + (stroke.Thickness / 2);

            if (InkGeometry.DistanceTo(stroke.Points, pagePoint.X, pagePoint.Y) > reach)
            {
                continue;
            }

            EnsureEraseUndo();
            var fragments = InkGeometry.Erase(stroke.Points, pagePoint.X, pagePoint.Y, reach);
            Overlay.InkStrokes.RemoveAt(index);

            foreach (var fragment in fragments)
            {
                Overlay.InkStrokes.Insert(index, new InkStrokeOverlay
                {
                    ColorHex = stroke.ColorHex,
                    Thickness = stroke.Thickness,
                    Points = fragment
                });
            }

            changed = true;
        }

        return EraseNonInk(pagePoint) || changed;
    }

    private bool EraseWhole(Point pagePoint)
    {
        for (var index = Overlay.InkStrokes.Count - 1; index >= 0; index--)
        {
            var stroke = Overlay.InkStrokes[index];
            if (InkGeometry.DistanceTo(stroke.Points, pagePoint.X, pagePoint.Y) <= EraserRadius + (stroke.Thickness / 2))
            {
                EnsureEraseUndo();
                Overlay.InkStrokes.RemoveAt(index);
                return true;
            }
        }

        return EraseNonInk(pagePoint);
    }

    private bool EraseNonInk(Point pagePoint)
    {
        for (var index = Overlay.Shapes.Count - 1; index >= 0; index--)
        {
            var shape = Overlay.Shapes[index];
            var reach = EraserRadius + (shape.Thickness / 2);

            if (ShapeGeometry.DistanceTo(shape, pagePoint.X, pagePoint.Y) <= reach ||
                ShapeGeometry.ContainsInterior(shape, pagePoint.X, pagePoint.Y))
            {
                EnsureEraseUndo();
                Overlay.Shapes.RemoveAt(index);
                return true;
            }
        }

        for (var index = Overlay.TextItems.Count - 1; index >= 0; index--)
        {
            var item = Overlay.TextItems[index];
            if (Contains(new Rect(item.X, item.Y, item.Width, item.Height), pagePoint))
            {
                EnsureEraseUndo();
                Overlay.TextItems.RemoveAt(index);
                return true;
            }
        }

        for (var index = Overlay.Signatures.Count - 1; index >= 0; index--)
        {
            var item = Overlay.Signatures[index];
            if (Contains(new Rect(item.X, item.Y, item.Width, item.Height), pagePoint))
            {
                EnsureEraseUndo();
                Overlay.Signatures.RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    /// <summary>One undo entry covers a whole eraser drag, not every sample along it.</summary>
    private void EnsureEraseUndo()
    {
        if (_erasePushedUndo)
        {
            return;
        }

        RecordEdit();
        _erasePushedUndo = true;
    }

    private void FinishErase()
    {
        var changed = _erasePushedUndo;
        _isErasing = false;
        _erasePushedUndo = false;
        _activePointerId = null;

        if (changed)
        {
            ClearSelection();
            NotifyOverlayChanged();
        }
    }

    private static bool Contains(Rect rect, Point point) =>
        point.X >= rect.X && point.X <= rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;

    // ═══════════ Helpers ═══════════

    private Point ToPagePosition(Point canvasPoint) =>
        new(canvasPoint.X / DisplayScale, canvasPoint.Y / DisplayScale);

    private Point ToCanvasPoint(PointOverlay point) =>
        new(point.X * DisplayScale, point.Y * DisplayScale);

    /// <summary>Converts a pointer sample to page units, capturing pen pressure when it is reported.</summary>
    private PointOverlay ToPagePoint(PointerPoint pointerPoint)
    {
        // Only pens carry meaningful pressure; mice and touch always draw at full width.
        var pressure = _activePointerDevice == PointerDeviceType.Pen
            ? Math.Clamp(pointerPoint.Properties.Pressure, 0.0, 1.0)
            : 1.0;

        return new PointOverlay
        {
            X = pointerPoint.Position.X / DisplayScale,
            Y = pointerPoint.Position.Y / DisplayScale,
            Pressure = pressure
        };
    }

    /// <summary>
    /// Digitizers without pressure support report a constant 0.5 for every sample, which would
    /// otherwise render the whole stroke at 67% of the chosen thickness. A stroke that never varies
    /// carries no pressure information, so it is normalised back to full width.
    /// </summary>
    private static void NormalizeUnsupportedPressure(List<PointOverlay> points)
    {
        if (points.Count == 0 || !InkGeometry.HasUniformPressure(points))
        {
            return;
        }

        foreach (var point in points)
        {
            point.Pressure = 1;
        }
    }

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

    /// <summary>Publishes the pre-edit state so the owning view model can push it onto the undo stack.</summary>
    private void RecordEdit() =>
        EditRecording?.Invoke(this, OverlayHistory.Clone(Overlay));

    private void NotifyOverlayChanged() =>
        OverlayChanged?.Invoke(this, OverlayHistory.Clone(Overlay));

    private static Border CreateToolbarShell(UIElement content) =>
        new()
        {
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 32, 32, 32)),
            CornerRadius = new CornerRadius(14),
            Visibility = Visibility.Collapsed,
            Child = content
        };

    private static ColorPicker CreateColorPicker(TypedEventHandler<ColorPicker, ColorChangedEventArgs> handler)
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

        picker.ColorChanged += handler;
        return picker;
    }

    /// <summary>
    /// Wraps a picker in a flyout that resets the undo debounce each time it opens, so dragging
    /// across the spectrum produces one undo entry rather than one per sampled colour.
    /// </summary>
    private Flyout CreateColorFlyout(ColorPicker picker)
    {
        var flyout = new Flyout { Content = picker };
        flyout.Opened += (_, _) => _colorPushedUndo = false;
        return flyout;
    }

    /// <summary>Records an undo entry only on the first colour change of a picker interaction.</summary>
    private void RecordColorEditOnce()
    {
        if (_colorPushedUndo)
        {
            return;
        }

        RecordEdit();
        _colorPushedUndo = true;
    }

    private static Border CreateColorDot() =>
        new()
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Colors.Black)
        };

    private static void SetColorDot(Button button, string colorHex)
    {
        if (button.Content is Border dot)
        {
            dot.Background = ColorBrushFromHex(colorHex);
        }
    }

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

    private static string ToHex(Windows.UI.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static SolidColorBrush ColorBrushFromHex(string colorHex) =>
        new(ParseColor(colorHex));

    /// <summary>A shape's interior brush, translucent so page content stays readable beneath it.</summary>
    private static SolidColorBrush FillBrushFromHex(string colorHex)
    {
        var color = ParseColor(colorHex);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(ShapeFillAlpha, color.R, color.G, color.B));
    }

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
