using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElliePdf.Helpers;
using ElliePdf.Models;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ElliePdf.ViewModels;

public sealed partial class EditPageViewModel : ObservableObject, IDisposable
{
    private readonly IDocumentTabService _tabService;
    private readonly IPdfService _pdfService;
    private readonly IAnnotationStore _annotationStore;
    private readonly IEditSaveService _editSaveService;
    private CancellationTokenSource? _renderCts;
    private double _viewportWidth = 800;

    public EditPageViewModel(
        IDocumentTabService tabService,
        IPdfService pdfService,
        IAnnotationStore annotationStore,
        IEditSaveService editSaveService)
    {
        _tabService = tabService;
        _pdfService = pdfService;
        _annotationStore = annotationStore;
        _editSaveService = editSaveService;
        _tabService.StateChanged += OnTabStateChanged;
    }

    [ObservableProperty]
    public partial BitmapImage? PageImage { get; private set; }

    [ObservableProperty]
    public partial bool IsInkModeEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; private set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial int PagePixelWidth { get; private set; }

    [ObservableProperty]
    public partial int PagePixelHeight { get; private set; }

    [ObservableProperty]
    public partial float PageWidthPoints { get; private set; }

    [ObservableProperty]
    public partial float PageHeightPoints { get; private set; }

    public bool HasDocument => _tabService.ActiveTab is not null;

    public string DocumentTitle => _tabService.ActiveFileName ?? "Open a PDF in Read first";

    public string PageLabel
    {
        get
        {
            var document = _tabService.ActiveDocument;
            if (document is null || document.PageCount == 0)
            {
                return "Page -/-";
            }

            return $"Page {_tabService.CurrentPageIndex + 1} / {document.PageCount}";
        }
    }

    public double DisplayScale => PageWidthPoints > 0 ? PagePixelWidth / PageWidthPoints : 1.0;

    public double ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            _viewportWidth = Math.Max(200, value);
            _ = RenderCurrentPageAsync();
        }
    }

    public PageOverlayState CurrentOverlay
    {
        get
        {
            var tab = _tabService.ActiveTab;
            if (tab is null)
            {
                return new PageOverlayState();
            }

            return _annotationStore.GetPageOverlay(tab.Id, _tabService.CurrentPageIndex);
        }
    }

    public async Task RefreshAsync()
    {
        var tab = _tabService.ActiveTab;
        if (tab is not null)
        {
            await _annotationStore.LoadCompanionAsync(tab.Id, tab.FilePath);
        }

        NotifyDocumentChanged();
        await RenderCurrentPageAsync();
        OnPropertyChanged(nameof(CurrentOverlay));
    }

    public void PersistCurrentOverlay(PageOverlayState overlay)
    {
        var tab = _tabService.ActiveTab;
        if (tab is null)
        {
            return;
        }

        _annotationStore.SetPageOverlay(tab.Id, _tabService.CurrentPageIndex, overlay);
        tab.IsDirty = true;
    }

    [RelayCommand]
    private void ToggleInkMode() => IsInkModeEnabled = !IsInkModeEnabled;

    [RelayCommand]
    private void PreviousPage()
    {
        _tabService.CurrentPageIndex -= 1;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void NextPage()
    {
        _tabService.CurrentPageIndex += 1;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var tab = _tabService.ActiveTab;
        if (tab is null)
        {
            SetStatus("Open a document before saving.", InfoBarSeverity.Informational);
            return;
        }

        var confirmed = await ConfirmOverwriteAsync(tab.FilePath);
        if (!confirmed)
        {
            return;
        }

        await SaveToPathAsync(tab, tab.FilePath);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var tab = _tabService.ActiveTab;
        if (tab is null)
        {
            SetStatus("Open a document before saving.", InfoBarSeverity.Informational);
            return;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(
            GetWindowId())
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(tab.FilePath) + "-edited"
        };
        picker.FileTypeChoices.Add("PDF Document", [".pdf"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await SaveToPathAsync(tab, file.Path);
        await _tabService.OpenOrActivateTabAsync(file.Path);
        SetStatus($"Saved a copy to '{Path.GetFileName(file.Path)}'.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    private void OpenRead() => Navigation.AppNavigation.RequestWorkspace("read");

    public void Dispose() => _tabService.StateChanged -= OnTabStateChanged;

    private async Task SaveToPathAsync(Services.DocumentTab tab, string outputPath)
    {
        IsBusy = true;

        try
        {
            await _editSaveService.SaveTabAsync(tab, outputPath, CancellationToken.None);
            SetStatus($"Saved '{Path.GetFileName(outputPath)}' with annotations embedded.", InfoBarSeverity.Success);
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task<bool> ConfirmOverwriteAsync(string path)
    {
        var xamlRoot = GetXamlRoot();
        if (xamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = "Save changes?",
            Content = $"Overwrite '{Path.GetFileName(path)}' with your edits?",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void OnTabStateChanged(object? sender, EventArgs e)
    {
        NotifyDocumentChanged();
        _ = RenderCurrentPageAsync();
        OnPropertyChanged(nameof(CurrentOverlay));
    }

    private void NotifyDocumentChanged()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(PageLabel));
    }

    private async Task RenderCurrentPageAsync()
    {
        var document = _tabService.ActiveDocument;
        if (document is null || document.PageCount == 0)
        {
            PageImage = null;
            return;
        }

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;
        var scale = ZoomScaleCalculator.ResolveScale(
            _tabService.ZoomMode,
            _tabService.ZoomScale,
            _viewportWidth);

        try
        {
            IsBusy = true;
            var rendered = await _pdfService.RenderPageAsync(document, _tabService.CurrentPageIndex, scale, token);
            PageImage = await BitmapHelper.CreateBitmapAsync(rendered.PngBytes);
            PagePixelWidth = rendered.Width;
            PagePixelHeight = rendered.Height;
            PageWidthPoints = rendered.PageWidthPoints;
            PageHeightPoints = rendered.PageHeightPoints;
            NotifyDocumentChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static Microsoft.UI.WindowId GetWindowId()
    {
        var hwnd = App.WindowHandle;
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return windowId;
    }

    private static Microsoft.UI.Xaml.XamlRoot? GetXamlRoot()
    {
        return App.Window.Content is Microsoft.UI.Xaml.FrameworkElement root ? root.XamlRoot : null;
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
