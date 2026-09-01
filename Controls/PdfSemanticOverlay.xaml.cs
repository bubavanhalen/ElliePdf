using ElliePdf.Pdf.Contracts;
using ElliePdf.Semantics;
using ElliePdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using ContractPdfRect = ElliePdf.Pdf.Contracts.PdfRect;

namespace ElliePdf.Controls;

/// <summary>
/// A recycled, handle-free semantic layer above PDF tiles. Real WinUI text, link and form
/// controls provide keyboard/UIA patterns while the raster surface remains virtualized.
/// </summary>
public sealed partial class PdfSemanticOverlay : UserControl
{
    public static readonly DependencyProperty SemanticPageProperty = DependencyProperty.Register(
        nameof(SemanticPage), typeof(object), typeof(PdfSemanticOverlay),
        new PropertyMetadata(null, OnProjectionChanged));

    public static readonly DependencyProperty PageHeightPointsProperty = DependencyProperty.Register(
        nameof(PageHeightPoints), typeof(float), typeof(PdfSemanticOverlay),
        new PropertyMetadata(0f, OnProjectionChanged));

    public static readonly DependencyProperty DisplayScaleProperty = DependencyProperty.Register(
        nameof(DisplayScale), typeof(double), typeof(PdfSemanticOverlay),
        new PropertyMetadata(1d, OnProjectionChanged));

    public static readonly DependencyProperty CanCopyProperty = DependencyProperty.Register(
        nameof(CanCopy), typeof(bool), typeof(PdfSemanticOverlay),
        new PropertyMetadata(false, OnProjectionChanged));

    private bool _building;
    private TextBox? _lastTextPointerBox;
    private ulong _lastTextPointerTimestamp;
    private Point _lastTextPointerPosition;
    private int _textPointerPressCount;

    public PdfSemanticOverlay() => InitializeComponent();

    public SemanticPageSnapshot? SemanticPage
    {
        get => GetValue(SemanticPageProperty) as SemanticPageSnapshot;
        set => SetValue(SemanticPageProperty, value);
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

    public bool CanCopy
    {
        get => (bool)GetValue(CanCopyProperty);
        set => SetValue(CanCopyProperty, value);
    }

    public event EventHandler<SemanticLinkInvokedEventArgs>? LinkInvoked;

    public event EventHandler<SemanticFormValueEventArgs>? FormValueCommitted;

    public event EventHandler<SemanticPushButtonInvokedEventArgs>? PushButtonInvoked;

    /// <summary>Raised after native TextBox selection changes; the host may coalesce adjacent page events.</summary>
    public event EventHandler<SemanticTextSelectionChangedEventArgs>? TextSelectionChanged;

    private static void OnProjectionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is PdfSemanticOverlay overlay)
        {
            overlay.Rebuild();
        }
    }

    private void Rebuild()
    {
        _lastTextPointerBox = null;
        _textPointerPressCount = 0;
        TextLayer.Children.Clear();
        LinkLayer.Children.Clear();
        FormLayer.Children.Clear();
        var page = SemanticPage;
        if (page is null || PageHeightPoints <= 0 || !double.IsFinite(DisplayScale) || DisplayScale <= 0)
        {
            AutomationProperties.SetName(this, AppResources.Get("Reader_SemanticPageLoading"));
            return;
        }

        AutomationProperties.SetName(
            this,
            AppResources.Format(
                "Reader_SemanticPageName",
                page.Metadata.PageIndex + 1,
                page.Text.Text.Length));
        _building = true;
        try
        {
            foreach (var line in SemanticTextSelection.VisualLines(page))
            {
                AddTextSpan(new TextSpan(line.Start, line.Text, line.Bounds, line.FontSize));
            }
            foreach (var link in page.Links)
            {
                AddLink(link);
            }
            foreach (var form in page.Forms)
            {
                AddForm(form);
            }
        }
        finally
        {
            _building = false;
        }
    }

    private void AddTextSpan(TextSpan span)
    {
        var box = new TextBox
        {
            Text = span.Text,
            IsReadOnly = true,
            IsTabStop = CanCopy,
            IsHitTestVisible = CanCopy,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Foreground = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            FontSize = Math.Clamp((span.FontSize ?? 11) * DisplayScale, 8, 96)
        };
        AutomationProperties.SetName(box, span.Text);
        AutomationProperties.SetHelpText(
            box,
            CanCopy
                ? AppResources.Get("Reader_TextCopyAllowed")
                : AppResources.Get("Reader_TextCopyBlocked"));
        box.PointerPressed += OnTextPointerPressed;
        box.SelectionChanged += (_, _) =>
        {
            if (_building || !CanCopy || SemanticPage is null || box.SelectionLength <= 0)
                return;

            var start = span.StartIndex + box.SelectionStart;
            var end = start + box.SelectionLength;
            var selection = SemanticTextSelection.Create(
                [SemanticPage],
                new TextPosition(SemanticPage.Metadata.PageIndex, start),
                new TextPosition(SemanticPage.Metadata.PageIndex, end));
            TextSelectionChanged?.Invoke(this, new SemanticTextSelectionChangedEventArgs(selection));
        };
        Place(TextLayer, box, span.Bounds, minimumTarget: false);
    }

    private void OnTextPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        var point = e.GetCurrentPoint(box);
        var elapsed = point.Timestamp >= _lastTextPointerTimestamp
            ? point.Timestamp - _lastTextPointerTimestamp
            : ulong.MaxValue;
        var deltaX = point.Position.X - _lastTextPointerPosition.X;
        var deltaY = point.Position.Y - _lastTextPointerPosition.Y;
        var isRepeatedPress = ReferenceEquals(box, _lastTextPointerBox)
            && elapsed <= 600_000
            && (deltaX * deltaX) + (deltaY * deltaY) <= 64;

        _textPointerPressCount = isRepeatedPress ? _textPointerPressCount + 1 : 1;
        _lastTextPointerBox = box;
        _lastTextPointerTimestamp = point.Timestamp;
        _lastTextPointerPosition = point.Position;

        // TextBox provides native word selection on the second press, including
        // touch selection handles. A third press selects the complete visual-line
        // span produced by PDFium.
        if (_textPointerPressCount >= 3)
        {
            _textPointerPressCount = 0;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (box.XamlRoot is not null)
                {
                    box.Focus(FocusState.Pointer);
                    box.SelectAll();
                }
            });
        }
    }

    private void AddLink(SemanticLinkSnapshot link)
    {
        var name = link.Kind == PdfLinkKind.Page
            ? AppResources.Format("Reader_InternalLinkName", (link.TargetPageIndex ?? 0) + 1)
            : AppResources.Format("Reader_ExternalLinkName", link.Uri ?? string.Empty);
        var button = new Button
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(24, 0, 0, 0)),
            Padding = new Thickness(0),
            IsTabStop = true,
            Content = string.Empty,
            Tag = link
        };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, link.IsSafeToActivate
            ? AppResources.Get("Reader_LinkActivationHelp")
            : link.BlockedReason ?? AppResources.Get("Reader_LinkBlocked"));
        button.Click += (_, _) => LinkInvoked?.Invoke(this, new SemanticLinkInvokedEventArgs(link));
        Place(LinkLayer, button, link.Bounds, minimumTarget: true);
    }

    private void AddForm(SemanticFormSnapshot form)
    {
        if (!form.IsSupported || form.Type is FormWidgetType.Signature or FormWidgetType.Unsupported)
        {
            AddUnsupportedForm(form);
            return;
        }

        Control control = form.Type switch
        {
            FormWidgetType.Text => CreateTextForm(form),
            FormWidgetType.Checkbox => CreateCheckForm(form, radio: false),
            FormWidgetType.RadioButton => CreateCheckForm(form, radio: true),
            FormWidgetType.ComboBox => CreateComboForm(form),
            FormWidgetType.ListBox => CreateListForm(form),
            FormWidgetType.PushButton => CreatePushButton(form),
            _ => CreateUnsupportedButton(form)
        };
        control.IsEnabled = !form.IsReadOnly;
        control.IsTabStop = true;
        AutomationProperties.SetName(control, form.FieldName);
        AutomationProperties.SetHelpText(control, form.IsReadOnly
            ? AppResources.Get("Reader_FormReadOnly")
            : form.IsRequired
                ? AppResources.Get("Reader_FormRequired")
                : AppResources.Get("Reader_FormEditable"));
        Place(FormLayer, control, form.Bounds, minimumTarget: true);
    }

    private TextBox CreateTextForm(SemanticFormSnapshot form)
    {
        var control = new TextBox { Text = form.Value.Text ?? string.Empty, Tag = form };
        control.LostFocus += (_, _) => Commit(form, FormValue.TextValue(control.Text));
        return control;
    }

    private Control CreateCheckForm(SemanticFormSnapshot form, bool radio)
    {
        ToggleButton control = radio ? new RadioButton() : new CheckBox();
        control.IsChecked = form.Value.Boolean ?? false;
        control.Tag = form;
        control.Click += (_, _) => Commit(form, FormValue.BooleanValue(control.IsChecked == true));
        return control;
    }

    private ComboBox CreateComboForm(SemanticFormSnapshot form)
    {
        var control = new ComboBox { ItemsSource = form.Options, Tag = form };
        control.SelectedItem = form.Value.Text;
        control.SelectionChanged += (_, _) =>
        {
            if (!_building && control.SelectedItem is string value)
            {
                Commit(form, FormValue.Choice(value));
            }
        };
        return control;
    }

    private ListBox CreateListForm(SemanticFormSnapshot form)
    {
        var control = new ListBox
        {
            ItemsSource = form.Options,
            SelectionMode = form.Value.Kind == FormValueKind.Choices
                ? SelectionMode.Multiple
                : SelectionMode.Single,
            Tag = form
        };
        foreach (var value in form.Value.Choices)
        {
            control.SelectedItems.Add(value);
        }
        if (form.Value.Kind == FormValueKind.Choice)
        {
            control.SelectedItem = form.Value.Text;
        }
        control.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (control.SelectionMode == SelectionMode.Multiple)
            {
                Commit(form, FormValue.MultipleChoices(control.SelectedItems.OfType<string>()));
            }
            else if (control.SelectedItem is string value)
            {
                Commit(form, FormValue.Choice(value));
            }
        };
        return control;
    }

    private Button CreatePushButton(SemanticFormSnapshot form)
    {
        var control = new Button { Content = form.FieldName, Tag = form };
        control.Click += (_, _) => InvokePushButton(form);
        return control;
    }

    private Button CreateUnsupportedButton(SemanticFormSnapshot form) => new()
    {
        Content = AppResources.Get("Reader_FormUnsupportedShort"),
        Tag = form
    };

    private void AddUnsupportedForm(SemanticFormSnapshot form)
    {
        var control = CreateUnsupportedButton(form);
        control.IsTabStop = true;
        AutomationProperties.SetName(
            control,
            AppResources.Format("Reader_FormUnsupportedName", form.FieldName));
        AutomationProperties.SetHelpText(
            control,
            form.UnsupportedReason ?? AppResources.Get("Reader_FormUnsupported"));
        Place(FormLayer, control, form.Bounds, minimumTarget: true);
    }

    private void Commit(SemanticFormSnapshot form, FormValue value)
    {
        if (!_building)
        {
            FormValueCommitted?.Invoke(this, new SemanticFormValueEventArgs(form, value));
        }
    }

    private void InvokePushButton(SemanticFormSnapshot form)
    {
        if (!_building)
        {
            PushButtonInvoked?.Invoke(this, new SemanticPushButtonInvokedEventArgs(form));
        }
    }

    private void Place(Canvas layer, FrameworkElement element, ContractPdfRect bounds, bool minimumTarget)
    {
        var projected = SemanticGeometryProjection.Project(
            bounds,
            SemanticPage!.Metadata.Geometry,
            DisplayScale);
        if (minimumTarget)
        {
            projected = projected.ExpandToMinimum(44, 44);
        }
        element.Width = projected.Width;
        element.Height = projected.Height;
        layer.Children.Add(element);
        Canvas.SetLeft(element, Math.Max(0, projected.X));
        Canvas.SetTop(element, Math.Max(0, projected.Y));
    }
}

public sealed class SemanticLinkInvokedEventArgs(SemanticLinkSnapshot link) : EventArgs
{
    public SemanticLinkSnapshot Link { get; } = link;
}

public sealed class SemanticFormValueEventArgs(SemanticFormSnapshot form, FormValue value) : EventArgs
{
    public SemanticFormSnapshot Form { get; } = form;
    public FormValue Value { get; } = value;
}

public sealed class SemanticPushButtonInvokedEventArgs(SemanticFormSnapshot form) : EventArgs
{
    public SemanticFormSnapshot Form { get; } = form;
}

public sealed class SemanticTextSelectionChangedEventArgs(SelectionState selection) : EventArgs
{
    public SelectionState Selection { get; } = selection;
}
