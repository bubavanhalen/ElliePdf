using ElliePdf.Models;
using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;
using Windows.System;

namespace ElliePdf.Pages;

public sealed partial class ReaderPage : Page
{
    private readonly List<List<Point>> _signatureStrokes = [];
    private List<Point>? _currentSignatureStroke;
    private (string Base64, double Aspect)? _typedSignature;
    private (string Base64, double Aspect)? _importedSignature;

    /// <summary>Script-like faces that ship with Windows, in order of preference.</summary>
    private static readonly string[] SignatureFontCandidates =
        ["Segoe Script", "Gabriola", "Ink Free", "Brush Script MT", "Segoe UI"];

    private PrintDocument? _printDocument;
    private IReadOnlyList<int> _printPageIndices = [];
    private int _printPageCursor;

    private readonly DispatcherTimer _chromeIdleTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private bool _isChromeHidden;
    private int _openFlyoutCount;
    private bool _isSyncingZoomSlider;

    public ReaderPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<ReaderViewModel>();
        DataContext = ViewModel;

        PageViewer.ViewportWidthChanged += OnViewportWidthChanged;
        PageViewer.ViewportHeightChanged += OnViewportHeightChanged;
        PageViewer.ZoomInRequested += (_, _) => ViewModel.ZoomInCommand.Execute(null);
        PageViewer.ZoomOutRequested += (_, _) => ViewModel.ZoomOutCommand.Execute(null);
        PageViewer.PagePointerPressed += (_, _) =>
        {
            if (ViewModel.IsReadMode)
            {
                ViewModel.ClosePanels();
            }
        };
        PageViewer.EditSurface.OverlayChanged += EditSurface_OverlayChanged;
        PageViewer.EditSurface.ActiveToolChangeRequested += EditSurface_ActiveToolChangeRequested;
        PageViewer.EditSurface.EditRecording += EditSurface_EditRecording;
        ViewModel.HistoryApplied += ViewModel_HistoryApplied;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        BtnClearSignature.Click += BtnClearSignature_Click;
        SignatureDialog.PrimaryButtonClick += SignatureDialog_PrimaryButtonClick;
        SignatureCanvas.PointerMoved += SignatureCanvas_PointerMoved;
        SignatureCanvas.PointerPressed += SignatureCanvas_PointerPressed;
        SignatureCanvas.PointerReleased += SignatureCanvas_PointerReleased;
        SignatureCanvas.PointerCaptureLost += SignatureCanvas_PointerCaptureLost;
        _chromeIdleTimer.Tick += ChromeIdleTimer_Tick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ReaderViewModel ViewModel { get; }

    public async Task LoadFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        await ViewModel.LoadFilesAsync(filePaths);
    }

    public void GoToPage(int pageIndex) => ViewModel.GoToPage(pageIndex);

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        RestartChromeTimer();
        await ViewModel.RefreshRecentFilesAsync();
        if (ViewModel.HasDocument)
        {
            await ViewModel.RefreshFromSessionAsync();
            LoadEditSurface();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _chromeIdleTimer.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        PageViewer.EditSurface.OverlayChanged -= EditSurface_OverlayChanged;
        PageViewer.EditSurface.ActiveToolChangeRequested -= EditSurface_ActiveToolChangeRequested;
        PageViewer.EditSurface.EditRecording -= EditSurface_EditRecording;
        ViewModel.HistoryApplied -= ViewModel_HistoryApplied;
    }

    private void OnViewportWidthChanged(object? sender, double width) => ViewModel.ViewportWidth = width;

    private void OnViewportHeightChanged(object? sender, double height) => ViewModel.ViewportHeight = height;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReaderViewModel.PageImage)
            or nameof(ReaderViewModel.CurrentOverlay))
        {
            LoadEditSurface();
        }
        else if (e.PropertyName is nameof(ReaderViewModel.IsEditMode))
        {
            if (!ViewModel.IsEditMode)
            {
                PageViewer.EditSurface.CommitActiveEdits();
            }

            LoadEditSurface();
        }
        else if (e.PropertyName is nameof(ReaderViewModel.ActiveEditTool)
            or nameof(ReaderViewModel.InkColorHex)
            or nameof(ReaderViewModel.InkThickness)
            or nameof(ReaderViewModel.EraserRadius)
            or nameof(ReaderViewModel.ErasePartially))
        {
            ApplyEditSurfaceState();
            UpdateInkPaletteSelection();
            if (!ViewModel.IsInkToolActive)
            {
                InkPalettePopup.IsOpen = false;
            }
        }
        else if (e.PropertyName == nameof(ReaderViewModel.IsThumbnailPanelOpen) && ViewModel.IsThumbnailPanelOpen)
        {
            AnimatePanelIn(ThumbnailsPanel, -24);
            ShowChrome();
        }
        else if (e.PropertyName == nameof(ReaderViewModel.IsOutlinePanelOpen) && ViewModel.IsOutlinePanelOpen)
        {
            AnimatePanelIn(OutlinePanel, -24);
            ShowChrome();
        }
        else if (e.PropertyName == nameof(ReaderViewModel.IsSearchPanelOpen) && ViewModel.IsSearchPanelOpen)
        {
            AnimatePanelIn(SearchPanel, 24);
            ShowChrome();
            SearchBox.Focus(FocusState.Programmatic);
        }
        else if (e.PropertyName == nameof(ReaderViewModel.ToolMode))
        {
            ShowChrome();
        }
    }

    private async void OpenFileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await PickAndOpenFileAsync();

    private async void CloseDocumentButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await CloseActiveTabAsync();

    private async Task PickAndOpenFileAsync()
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".pdf");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        ViewModel.ClosePanels();
        await ViewModel.LoadDocumentAsync(file.Path);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ViewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void PageThumbnails_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PageThumbnailViewModel thumbnail)
        {
            ViewModel.GoToThumbnailPageCommand.Execute(thumbnail);
        }
    }

    private void OutlineItems_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OutlineItemViewModel item)
        {
            ViewModel.GoToOutlineItemCommand.Execute(item);
        }
    }

    private async void RecentFiles_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentFileItemViewModel item)
        {
            ViewModel.ClosePanels();
            await ViewModel.OpenRecentCommand.ExecuteAsync(item);
        }
    }

    private void LoadEditSurface()
    {
        PageViewer.EditSurface.LoadOverlay(
            ViewModel.CurrentOverlay,
            ViewModel.DisplayScale,
            ViewModel.PagePixelWidth,
            ViewModel.PagePixelHeight);
        ApplyEditSurfaceState();
    }

    private void ApplyEditSurfaceState()
    {
        PageViewer.EditSurface.ActiveTool = ViewModel.ActiveEditTool;
        PageViewer.EditSurface.InkColorHex = ViewModel.InkColorHex;
        PageViewer.EditSurface.InkThickness = ViewModel.InkThickness;
        PageViewer.EditSurface.EraserRadius = ViewModel.EraserRadius;
        PageViewer.EditSurface.ErasePartially = ViewModel.ErasePartially;
    }

    private void EditSurface_EditRecording(object? sender, PageOverlayState before) =>
        ViewModel.RecordHistory(before);

    private void ViewModel_HistoryApplied(object? sender, Services.OverlaySnapshot snapshot) =>
        PageViewer.EditSurface.ApplyHistoryState(snapshot.State);

    private void EditSurface_OverlayChanged(object? sender, PageOverlayState overlay) =>
        ViewModel.PersistCurrentOverlay(overlay);

    private void EditSurface_ActiveToolChangeRequested(object? sender, ReaderEditTool tool)
    {
        switch (tool)
        {
            case ReaderEditTool.Select:
                ViewModel.UseSelectToolCommand.Execute(null);
                break;
            case ReaderEditTool.Text:
                ViewModel.UseTextToolCommand.Execute(null);
                break;
            case ReaderEditTool.Ink:
                ViewModel.UseInkToolCommand.Execute(null);
                break;
            case ReaderEditTool.Eraser:
                ViewModel.UseEraserToolCommand.Execute(null);
                break;
            case ReaderEditTool.Signature:
                ViewModel.UseSignatureToolCommand.Execute(null);
                break;
        }

        ApplyEditSurfaceState();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        PageViewer.EditSurface.CommitActiveEdits();
        await ViewModel.SaveCommand.ExecuteAsync(null);
    }

    private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        PageViewer.EditSurface.CommitActiveEdits();
        await ViewModel.SaveAsCommand.ExecuteAsync(null);
        LoadEditSurface();
    }

    private void AddTextButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UseTextToolCommand.Execute(null);
        ApplyEditSurfaceState();
    }

    private void InkToolButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditSurfaceState();
        UpdateInkPaletteSelection();
        OpenInkPalette();
    }

    private void OpenInkPalette() => OpenPalette(InkPalettePopup, InkPalette, InkToolButton);

    /// <summary>Anchors a palette popup above the tool button that opened it.</summary>
    private void OpenPalette(Popup popup, FrameworkElement palette, FrameworkElement anchorElement)
    {
        palette.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = palette.DesiredSize;
        var anchor = anchorElement.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
        var left = anchor.X + (anchorElement.ActualWidth / 2) - (desired.Width / 2);
        var top = anchor.Y - desired.Height - 10;

        popup.HorizontalOffset = Math.Clamp(left, 8, Math.Max(8, RootGrid.ActualWidth - desired.Width - 8));
        popup.VerticalOffset = Math.Max(8, top);
        popup.IsOpen = true;
    }

    private void InkColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string colorHex })
        {
            ViewModel.SetInkColorCommand.Execute(colorHex);
            ApplyEditSurfaceState();
            UpdateInkPaletteSelection();
        }
    }

    private void InkThicknessButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var thickness))
        {
            ViewModel.SetInkThicknessCommand.Execute(thickness);
            ApplyEditSurfaceState();
            UpdateInkPaletteSelection();
        }
    }

    private void UpdateInkPaletteSelection()
    {
        UpdatePaletteButton(InkBlackButton, ViewModel.InkColorHex == "#000000");
        UpdatePaletteButton(InkRedButton, ViewModel.InkColorHex == "#B3261E");
        UpdatePaletteButton(InkBlueButton, ViewModel.InkColorHex == "#1A73E8");
        UpdatePaletteButton(InkThinButton, Math.Abs(ViewModel.InkThickness - 2) < 0.1);
        UpdatePaletteButton(InkMediumButton, Math.Abs(ViewModel.InkThickness - 5) < 0.1);
        UpdatePaletteButton(InkThickButton, Math.Abs(ViewModel.InkThickness - 9) < 0.1);
        UpdatePaletteButton(EraserSmallButton, Math.Abs(ViewModel.EraserRadius - 6) < 0.1);
        UpdatePaletteButton(EraserMediumButton, Math.Abs(ViewModel.EraserRadius - 14) < 0.1);
        UpdatePaletteButton(EraserLargeButton, Math.Abs(ViewModel.EraserRadius - 26) < 0.1);
    }

    private void EraserToolButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditSurfaceState();
        UpdateInkPaletteSelection();
        OpenPalette(EraserPalettePopup, EraserPalette, EraserToolButton);
    }

    private void EraserRadiusButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var radius))
        {
            ViewModel.SetEraserRadiusCommand.Execute(radius);
            ApplyEditSurfaceState();
            UpdateInkPaletteSelection();
        }
    }

    private void ErasePartialToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleErasePartiallyCommand.Execute(null);
        ApplyEditSurfaceState();
    }

    private static void UpdatePaletteButton(Button button, bool isSelected)
    {
        button.BorderBrush = isSelected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xDC, 0xAE, 0x96))
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Background = isSelected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xDC, 0xAE, 0x96))
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void UndoEditButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.UndoEditCommand.Execute(null);

    private void RedoEditButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RedoEditCommand.Execute(null);

    private void DeleteEditButton_Click(object sender, RoutedEventArgs e) =>
        PageViewer.EditSurface.DeleteSelection();

    private async void SignatureButton_Click(object sender, RoutedEventArgs e)
    {
        _signatureStrokes.Clear();
        _currentSignatureStroke = null;
        _importedSignature = null;
        _typedSignature = null;
        SignatureCanvas.Children.Clear();
        ImportedSignaturePreview.Source = null;
        TypedSignaturePreview.Source = null;
        TypedSignatureBox.Text = string.Empty;

        InitializeSignatureFonts();
        await LoadSavedSignaturesAsync();

        SignaturePivot.SelectedIndex = 0;
        SignatureDialog.XamlRoot = XamlRoot;
        await SignatureDialog.ShowAsync();
    }

    private void InitializeSignatureFonts()
    {
        if (TypedSignatureFontBox.Items.Count > 0)
        {
            return;
        }

        foreach (var family in SignatureFontCandidates)
        {
            TypedSignatureFontBox.Items.Add(family);
        }

        TypedSignatureFontBox.SelectedIndex = 0;
    }

    private async Task LoadSavedSignaturesAsync()
    {
        var saved = ViewModel.GetSavedSignatures();
        var items = new List<SavedSignatureViewModel>();

        foreach (var signature in saved)
        {
            BitmapImage? preview = null;
            try
            {
                preview = await Helpers.BitmapHelper.CreateBitmapAsync(Convert.FromBase64String(signature.ImageBase64));
            }
            catch (FormatException)
            {
                // A corrupt entry simply does not get a thumbnail.
            }

            items.Add(new SavedSignatureViewModel(signature, preview));
        }

        SavedSignaturesGrid.ItemsSource = items;
        NoSavedSignaturesText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedSignaturesGrid.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void SavedSignaturesGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SavedSignatureViewModel item)
        {
            return;
        }

        SignatureDialog.Hide();
        PlaceSignature(item.ImageBase64, item.AspectRatio);
        await Task.CompletedTask;
    }

    private async void DeleteSavedSignature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ViewModel.DeleteSavedSignature(id);
            await LoadSavedSignaturesAsync();
        }
    }

    private async void TypedSignatureBox_TextChanged(object sender, TextChangedEventArgs e) =>
        await RefreshTypedSignatureAsync();

    private async void TypedSignatureFontBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await RefreshTypedSignatureAsync();

    private async Task RefreshTypedSignatureAsync()
    {
        var text = TypedSignatureBox.Text;
        var font = TypedSignatureFontBox.SelectedItem as string ?? SignatureFontCandidates[0];

        if (!Helpers.SignatureRenderer.TryRenderTyped(text, font, out var png, out var aspect))
        {
            _typedSignature = null;
            TypedSignaturePreview.Source = null;
            return;
        }

        _typedSignature = (Convert.ToBase64String(png), aspect);
        TypedSignaturePreview.Source = await Helpers.BitmapHelper.CreateBitmapAsync(png);
    }

    private async void ImportSignatureButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };

        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(file.Path);

            // Decoding and alpha-keying a photo is slow enough to freeze the window.
            var result = await Task.Run(() =>
                Helpers.SignatureRenderer.TryImport(bytes, out var png, out var aspect)
                    ? (Png: png, Aspect: aspect)
                    : ((byte[] Png, double Aspect)?)null);

            if (result is not { } imported)
            {
                ViewModel.ReportSignatureImportFailed();
                return;
            }

            _importedSignature = (Convert.ToBase64String(imported.Png), imported.Aspect);
            ImportedSignaturePreview.Source = await Helpers.BitmapHelper.CreateBitmapAsync(imported.Png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.ReportSignatureImportFailed();
        }
    }

    private void BtnClearSignature_Click(object sender, RoutedEventArgs e)
    {
        _signatureStrokes.Clear();
        _currentSignatureStroke = null;
        SignatureCanvas.Children.Clear();
    }

    private void SignatureCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(SignatureCanvas).Position;
        _currentSignatureStroke = [point];
        _signatureStrokes.Add(_currentSignatureStroke);
        SignatureCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SignatureCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_currentSignatureStroke is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(SignatureCanvas).Position;
        var last = _currentSignatureStroke[^1];
        if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) < 1.0)
        {
            return;
        }

        _currentSignatureStroke.Add(point);
        RedrawSignatureCanvas();
        e.Handled = true;
    }

    private void SignatureCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_currentSignatureStroke is null)
        {
            return;
        }

        SignatureCanvas.ReleasePointerCapture(e.Pointer);
        _currentSignatureStroke = null;
        RedrawSignatureCanvas();
        e.Handled = true;
    }

    private void SignatureCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        _currentSignatureStroke = null;

    private void RedrawSignatureCanvas()
    {
        SignatureCanvas.Children.Clear();
        foreach (var stroke in _signatureStrokes)
        {
            if (stroke.Count < 2)
            {
                continue;
            }

            var points = new PointCollection();
            foreach (var point in stroke)
            {
                points.Add(point);
            }

            SignatureCanvas.Children.Add(new Polyline
            {
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Black),
                StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Points = points
            });
        }
    }

    private void SignatureDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var selection = SignaturePivot.SelectedIndex switch
        {
            0 => CaptureDrawnSignature(),
            1 => _typedSignature is { } typed ? (typed.Base64, typed.Aspect, SaveTypedSignatureCheckBox.IsChecked == true) : null,
            3 => _importedSignature is { } imported ? (imported.Base64, imported.Aspect, SaveImportedSignatureCheckBox.IsChecked == true) : null,
            _ => null
        };

        if (selection is not { } chosen)
        {
            // Nothing usable on the active tab, so keep the dialog open.
            args.Cancel = true;
            return;
        }

        if (chosen.Save)
        {
            ViewModel.SaveSignature(chosen.Base64, chosen.Aspect);
        }

        PlaceSignature(chosen.Base64, chosen.Aspect);
    }

    private (string Base64, double Aspect, bool Save)? CaptureDrawnSignature()
    {
        // Rasterize while the strokes are still in hand; the canvas leaves the tree as the dialog closes.
        var strokes = _signatureStrokes
            .Select(stroke => (IReadOnlyList<Helpers.StrokePoint>)stroke
                .Select(point => new Helpers.StrokePoint(point.X, point.Y))
                .ToList())
            .ToList();

        if (!Helpers.SignatureRenderer.TryRender(strokes, out var pngBytes, out var aspectRatio))
        {
            return null;
        }

        return (Convert.ToBase64String(pngBytes), aspectRatio, SaveSignatureCheckBox.IsChecked == true);
    }

    private void PlaceSignature(string base64, double aspectRatio)
    {
        ViewModel.UseSelectToolCommand.Execute(null);
        ApplyEditSurfaceState();
        PageViewer.EditSurface.PlaceSignature(base64, aspectRatio);
    }

    private async void PrintButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!ViewModel.HasDocument)
        {
            return;
        }

        if (!PrintManager.IsSupported())
        {
            return;
        }

        var range = await PromptPrintRangeAsync();
        if (range is null)
        {
            return;
        }

        _printPageIndices = range;
        _printPageCursor = 0;

        _printDocument = new PrintDocument();
        _printDocument.Paginate += OnPrintPaginate;
        _printDocument.GetPreviewPage += OnPrintGetPreviewPage;
        _printDocument.AddPages += OnPrintAddPages;

        var printManager = PrintManager.GetForCurrentView();
        printManager.PrintTaskRequested += OnPrintTaskRequested;

        try
        {
            await PrintManager.ShowPrintUIAsync();
        }
        finally
        {
            printManager.PrintTaskRequested -= OnPrintTaskRequested;
            if (_printDocument is not null)
            {
                _printDocument.Paginate -= OnPrintPaginate;
                _printDocument.GetPreviewPage -= OnPrintGetPreviewPage;
                _printDocument.AddPages -= OnPrintAddPages;
                _printDocument = null;
            }

            _printPageIndices = [];
            _printPageCursor = 0;
        }
    }

    private async Task<IReadOnlyList<int>?> PromptPrintRangeAsync()
    {
        var allPagesRadio = new RadioButton
        {
            Content = "All pages",
            IsChecked = true,
            Tag = "all"
        };
        var currentPageRadio = new RadioButton
        {
            Content = "Current page",
            Tag = "current"
        };
        var customRangeRadio = new RadioButton
        {
            Content = "Page range",
            Tag = "range"
        };
        var fromBox = new NumberBox
        {
            Header = "From",
            Minimum = 1,
            Maximum = ViewModel.DocumentPageCount,
            Value = ViewModel.DocumentPageCount > 0 ? 1 : 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            IsEnabled = false
        };
        var toBox = new NumberBox
        {
            Header = "To",
            Minimum = 1,
            Maximum = ViewModel.DocumentPageCount,
            Value = ViewModel.DocumentPageCount,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            IsEnabled = false
        };

        void UpdateRangeInputs(RadioButton selected)
        {
            var isCustom = ReferenceEquals(selected, customRangeRadio);
            fromBox.IsEnabled = isCustom;
            toBox.IsEnabled = isCustom;
        }

        allPagesRadio.Checked += (_, _) => UpdateRangeInputs(allPagesRadio);
        currentPageRadio.Checked += (_, _) => UpdateRangeInputs(currentPageRadio);
        customRangeRadio.Checked += (_, _) => UpdateRangeInputs(customRangeRadio);

        var dialog = new ContentDialog
        {
            Title = "Print",
            PrimaryButtonText = "Print",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    allPagesRadio,
                    currentPageRadio,
                    customRangeRadio,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children = { fromBox, toBox }
                    }
                }
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        if (currentPageRadio.IsChecked == true)
        {
            return [Math.Clamp(ViewModel.CurrentPageIndex, 0, Math.Max(0, ViewModel.DocumentPageCount - 1))];
        }

        if (customRangeRadio.IsChecked == true)
        {
            var from = (int)Math.Clamp(fromBox.Value, 1, ViewModel.DocumentPageCount) - 1;
            var to = (int)Math.Clamp(toBox.Value, from + 1, ViewModel.DocumentPageCount) - 1;
            return Enumerable.Range(from, to - from + 1).ToArray();
        }

        return Enumerable.Range(0, ViewModel.DocumentPageCount).ToArray();
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        args.Request.CreatePrintTask("ElliePdf", sourceRequested =>
        {
            if (_printDocument is not null)
            {
                sourceRequested.SetSource(_printDocument.DocumentSource);
            }
        });
        deferral.Complete();
    }

    private void OnPrintPaginate(object? sender, PaginateEventArgs e)
    {
        if (_printDocument is null)
        {
            return;
        }

        _printDocument.SetPreviewPageCount(_printPageIndices.Count, PreviewPageCountType.Final);
    }

    private async void OnPrintGetPreviewPage(object? sender, GetPreviewPageEventArgs e)
    {
        if (_printDocument is null || e.PageNumber < 1 || e.PageNumber > _printPageIndices.Count)
        {
            return;
        }

        var page = await CreatePrintPageAsync(_printPageIndices[e.PageNumber - 1]);
        if (page is not null)
        {
            _printDocument.SetPreviewPage(e.PageNumber, page);
        }
    }

    private async void OnPrintAddPages(object? sender, AddPagesEventArgs e)
    {
        if (_printDocument is null)
        {
            return;
        }

        while (_printPageCursor < _printPageIndices.Count)
        {
            var page = await CreatePrintPageAsync(_printPageIndices[_printPageCursor]);
            if (page is not null)
            {
                _printDocument.AddPage(page);
            }

            _printPageCursor++;
        }

        _printDocument.AddPagesComplete();
    }

    private async Task<Grid?> CreatePrintPageAsync(int pageIndex)
    {
        var image = await ViewModel.RenderPageImageAsync(pageIndex, 96.0 / 72.0);
        if (image is null)
        {
            return null;
        }

        return CreatePrintPage(image);
    }

    private static Grid CreatePrintPage(BitmapImage image)
    {
        var page = new Grid
        {
            Width = 816,
            Height = 1056,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
        };

        page.Children.Add(new Image
        {
            Source = image,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        });

        return page;
    }

    private void ReaderPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var controlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ViewModel.IsEditMode)
        {
            if (e.OriginalSource is TextBox)
            {
                return;
            }

            if (e.Key == VirtualKey.Delete)
            {
                PageViewer.EditSurface.DeleteSelection();
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Escape)
            {
                ViewModel.UseSelectToolCommand.Execute(null);
                PageViewer.EditSurface.ClearSelection();
                ApplyEditSurfaceState();
                e.Handled = true;
                return;
            }

            if (controlDown && e.Key == VirtualKey.Z)
            {
                var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                if (shiftDown)
                {
                    ViewModel.RedoEditCommand.Execute(null);
                }
                else
                {
                    ViewModel.UndoEditCommand.Execute(null);
                }

                e.Handled = true;
                return;
            }

            if (controlDown && e.Key == VirtualKey.Y)
            {
                ViewModel.RedoEditCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (!controlDown)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.O:
                _ = PickAndOpenFileAsync();
                e.Handled = true;
                break;
            case VirtualKey.W:
                _ = CloseActiveTabAsync();
                e.Handled = true;
                break;
            case VirtualKey.F:
                ViewModel.ToggleSearchPanelCommand.Execute(null);
                if (ViewModel.IsSearchPanelOpen)
                {
                    SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                }

                e.Handled = true;
                break;
            case VirtualKey.P:
                PrintButton_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs());
                e.Handled = true;
                break;
            case VirtualKey.Add:
            case (VirtualKey)187:
                ViewModel.ZoomInCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Subtract:
            case (VirtualKey)189:
                ViewModel.ZoomOutCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private async Task CloseActiveTabAsync()
    {
        if (ViewModel.SelectedTabId is not Guid tabId)
        {
            return;
        }

        await ViewModel.TryCloseTabAsync(tabId);
    }

    // ═══════════ Chrome auto-hide ═══════════

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowChrome();

    private void ChromeIdleTimer_Tick(object? sender, object e)
    {
        _chromeIdleTimer.Stop();

        var canHide = ViewModel.HasDocument
            && ViewModel.IsReadMode
            && _openFlyoutCount == 0
            && !ViewModel.IsThumbnailPanelOpen
            && !ViewModel.IsOutlinePanelOpen
            && !ViewModel.IsSearchPanelOpen;

        if (!canHide)
        {
            return;
        }

        _isChromeHidden = true;
        FadeChrome(ReadToolbar, 0, 12);
        FadeChrome(TopCommandBar, 0, -12);
        ReadToolbar.IsHitTestVisible = false;
        TopCommandBar.IsHitTestVisible = false;
    }

    private void ShowChrome()
    {
        if (_isChromeHidden)
        {
            _isChromeHidden = false;
            FadeChrome(ReadToolbar, 1, 0);
            FadeChrome(TopCommandBar, 1, 0);
            ReadToolbar.IsHitTestVisible = true;
            TopCommandBar.IsHitTestVisible = true;
        }

        RestartChromeTimer();
    }

    private void RestartChromeTimer()
    {
        _chromeIdleTimer.Stop();
        _chromeIdleTimer.Start();
    }

    private static void FadeChrome(FrameworkElement element, double toOpacity, double toOffsetY)
    {
        if (element.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(260));
        var storyboard = new Storyboard();

        var fade = new DoubleAnimation
        {
            To = toOpacity,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var slide = new DoubleAnimation
        {
            To = toOffsetY,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");
        storyboard.Children.Add(slide);

        storyboard.Begin();
    }

    private static void AnimatePanelIn(FrameworkElement panel, double fromOffsetX)
    {
        if (panel.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            panel.RenderTransform = transform;
        }

        panel.Opacity = 0;
        transform.X = fromOffsetX;

        var duration = new Duration(TimeSpan.FromMilliseconds(240));
        var storyboard = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, panel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var slide = new DoubleAnimation
        {
            From = fromOffsetX,
            To = 0,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");
        storyboard.Children.Add(slide);

        storyboard.Begin();
    }

    // ═══════════ Flyouts ═══════════

    private void Flyout_Opening(object? sender, object e)
    {
        _openFlyoutCount++;
        ShowChrome();
    }

    private void Flyout_Closed(object? sender, object e)
    {
        _openFlyoutCount = Math.Max(0, _openFlyoutCount - 1);
        RestartChromeTimer();
    }

    private void ZoomFlyout_Opening(object? sender, object e)
    {
        Flyout_Opening(sender, e);
        _isSyncingZoomSlider = true;
        ZoomSlider.Value = Math.Clamp(Math.Round(ViewModel.EffectiveZoomScale * 100), 25, 400);
        _isSyncingZoomSlider = false;
    }

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // ViewModel is null while InitializeComponent coerces the slider's initial value.
        if (!_isSyncingZoomSlider && ViewModel is not null)
        {
            ViewModel.SetZoomPercentCommand.Execute(e.NewValue);
        }
    }

    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset })
        {
            return;
        }

        switch (preset)
        {
            case "fitwidth":
                ViewModel.ZoomFitWidthCommand.Execute(null);
                break;
            case "fitpage":
                ViewModel.ZoomFitPageCommand.Execute(null);
                break;
            default:
                ViewModel.ZoomActualSizeCommand.Execute(null);
                break;
        }

        ZoomFlyout.Hide();
    }

    private void GoToPageFlyout_Opening(object? sender, object e)
    {
        Flyout_Opening(sender, e);
        GoToPageBox.Maximum = Math.Max(1, ViewModel.DocumentPageCount);
        GoToPageBox.Value = ViewModel.CurrentPageIndex + 1;
    }

    private void GoToPageBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitGoToPage();
            e.Handled = true;
        }
    }

    private void GoToPageButton_Click(object sender, RoutedEventArgs e) => CommitGoToPage();

    private void CommitGoToPage()
    {
        if (!double.IsNaN(GoToPageBox.Value) && ViewModel.DocumentPageCount > 0)
        {
            var pageIndex = (int)Math.Clamp(GoToPageBox.Value, 1, ViewModel.DocumentPageCount) - 1;
            ViewModel.GoToPage(pageIndex);
        }

        GoToPageFlyout.Hide();
    }

    // ═══════════ Drag & drop ═══════════

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "Open in ElliePdf";
            }
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items
            .OfType<Windows.Storage.StorageFile>()
            .Where(file => string.Equals(file.FileType, ".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (paths.Length > 0)
        {
            ViewModel.ClosePanels();
            await ViewModel.LoadFilesAsync(paths);
        }
    }
}
