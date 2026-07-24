using ElliePdf.Models;
using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;
using Windows.System;

namespace ElliePdf.Pages;

public sealed partial class ReaderPage : Page
{
    private bool _isSyncingTabs;
    private readonly List<List<Point>> _signatureStrokes = [];
    private List<Point>? _currentSignatureStroke;

    private PrintDocument? _printDocument;
    private IReadOnlyList<int> _printPageIndices = [];
    private int _printPageCursor;

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
        ViewModel.TabItems.CollectionChanged += OnTabItemsChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        BtnClearSignature.Click += BtnClearSignature_Click;
        SignatureDialog.PrimaryButtonClick += SignatureDialog_PrimaryButtonClick;
        SignatureCanvas.PointerMoved += SignatureCanvas_PointerMoved;
        SignatureCanvas.PointerPressed += SignatureCanvas_PointerPressed;
        SignatureCanvas.PointerReleased += SignatureCanvas_PointerReleased;
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
        SyncTabViewItems();
    }

    public void GoToPage(int pageIndex) => ViewModel.GoToPage(pageIndex);

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        SyncTabViewItems();
        await ViewModel.RefreshRecentFilesAsync();
        if (ViewModel.HasDocument)
        {
            await ViewModel.RefreshFromSessionAsync();
            LoadEditSurface();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.TabItems.CollectionChanged -= OnTabItemsChanged;
        PageViewer.EditSurface.OverlayChanged -= EditSurface_OverlayChanged;
        PageViewer.EditSurface.ActiveToolChangeRequested -= EditSurface_ActiveToolChangeRequested;
    }

    private void OnViewportWidthChanged(object? sender, double width) => ViewModel.ViewportWidth = width;

    private void OnViewportHeightChanged(object? sender, double height) => ViewModel.ViewportHeight = height;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReaderViewModel.PageImage)
            or nameof(ReaderViewModel.CurrentOverlay)
            or nameof(ReaderViewModel.IsEditMode))
        {
            LoadEditSurface();
        }
        else if (e.PropertyName is nameof(ReaderViewModel.ActiveEditTool)
            or nameof(ReaderViewModel.InkColorHex)
            or nameof(ReaderViewModel.InkThickness))
        {
            ApplyEditSurfaceState();
            UpdateInkPaletteSelection();
            if (!ViewModel.IsInkToolActive)
            {
                InkPalettePopup.IsOpen = false;
            }
        }
    }

    private void OnTabItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_isSyncingTabs)
        {
            SyncTabViewItems();
        }
    }

    private async void OpenFileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await PickAndOpenFileAsync();

    private async void CloseDocumentButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await CloseActiveTabAsync();

    private async void DocumentTabs_AddTabButtonClick(TabView sender, object args) =>
        await PickAndOpenFileAsync();

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
        SyncTabViewItems();
    }

    private async void DocumentTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab?.Tag is not Guid tabId)
        {
            return;
        }

        var tabItem = args.Tab;
        if (!await ViewModel.TryCloseTabAsync(tabId))
        {
            return;
        }

        if (sender.TabItems.Contains(tabItem))
        {
            sender.TabItems.Remove(tabItem);
        }

        SyncTabViewItems();
    }

    private async void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingTabs || DocumentTabs.SelectedItem is not TabViewItem item || item.Tag is not Guid tabId)
        {
            return;
        }

        ViewModel.ClosePanels();
        await ViewModel.ActivateTabAsync(tabId);
    }

    private void SyncTabViewItems()
    {
        _isSyncingTabs = true;
        try
        {
            DocumentTabs.TabItems.Clear();

            foreach (var tab in ViewModel.TabItems)
            {
                DocumentTabs.TabItems.Add(new TabViewItem
                {
                    Header = tab.Title,
                    IsClosable = true,
                    Tag = tab.TabId
                });
            }

            if (ViewModel.SelectedTabId is Guid selectedId)
            {
                var selectedItem = DocumentTabs.TabItems
                    .OfType<TabViewItem>()
                    .FirstOrDefault(item => item.Tag is Guid id && id == selectedId);

                if (selectedItem is not null)
                {
                    DocumentTabs.SelectedItem = selectedItem;
                }
            }
        }
        finally
        {
            _isSyncingTabs = false;
        }
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
            SyncTabViewItems();
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
    }

    private void EditSurface_OverlayChanged(object? sender, PageOverlayState overlay) =>
        ViewModel.PersistCurrentOverlay(overlay);

    private void EditSurface_ActiveToolChangeRequested(object? sender, ReaderEditTool tool)
    {
        if (tool == ReaderEditTool.Select)
        {
            ViewModel.UseSelectToolCommand.Execute(null);
            ApplyEditSurfaceState();
        }
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

    private void OpenInkPalette()
    {
        InkPalette.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = InkPalette.DesiredSize;
        var anchor = InkToolButton.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
        var left = anchor.X + InkToolButton.ActualWidth / 2 - desired.Width / 2;
        var top = anchor.Y - desired.Height - 10;

        InkPalettePopup.HorizontalOffset = Math.Clamp(left, 8, Math.Max(8, RootGrid.ActualWidth - desired.Width - 8));
        InkPalettePopup.VerticalOffset = Math.Max(8, top);
        InkPalettePopup.IsOpen = true;
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
    }

    private static void UpdatePaletteButton(Button button, bool isSelected)
    {
        button.BorderBrush = isSelected
            ? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Background = isSelected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 30, 144, 255))
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void UndoEditButton_Click(object sender, RoutedEventArgs e) =>
        PageViewer.EditSurface.Undo();

    private void DeleteEditButton_Click(object sender, RoutedEventArgs e) =>
        PageViewer.EditSurface.DeleteSelection();

    private async void SignatureButton_Click(object sender, RoutedEventArgs e)
    {
        _signatureStrokes.Clear();
        _currentSignatureStroke = null;
        SignatureCanvas.Children.Clear();
        SignatureDialog.XamlRoot = XamlRoot;
        await SignatureDialog.ShowAsync();
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

        _currentSignatureStroke.Add(e.GetCurrentPoint(SignatureCanvas).Position);
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
        e.Handled = true;
    }

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
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Points = points
            });
        }
    }

    private void SignatureDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_signatureStrokes.All(stroke => stroke.Count < 2))
        {
            args.Cancel = true;
            return;
        }

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

        ViewModel.UseSignatureToolCommand.Execute(null);
        ApplyEditSurfaceState();
        PageViewer.EditSurface.PlaceSignature(Convert.ToBase64String(memory.ToArray()));
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
                PageViewer.EditSurface.Undo();
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

        if (!await ViewModel.TryCloseTabAsync(tabId))
        {
            return;
        }

        SyncTabViewItems();
    }
}
