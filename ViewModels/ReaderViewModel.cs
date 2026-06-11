using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElliePdf.Helpers;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ElliePdf.ViewModels;

public sealed partial class ReaderViewModel : ObservableObject, IDisposable
{
    private readonly IDocumentTabService _tabService;
    private readonly IPdfService _pdfService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly ITabCloseService _tabCloseService;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _searchCts;
    private double _viewportWidth = 800;
    private IReadOnlyList<TextMatch> _searchMatches = [];
    private int _activeSearchMatchIndex = -1;
    private byte[]? _lastRenderedPng;

    public ObservableCollection<DocumentTabItemViewModel> TabItems { get; } = [];

    public ObservableCollection<PageThumbnailViewModel> PageThumbnails { get; } = [];

    public ObservableCollection<RecentFileItemViewModel> RecentFiles { get; } = [];

    [ObservableProperty]
    public partial Guid? SelectedTabId { get; private set; }

    [ObservableProperty]
    public partial bool IsThumbnailPanelOpen { get; set; }

    [ObservableProperty]
    public partial bool IsSearchPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTabBar))]
    public partial int TabCount { get; private set; }

    public ReaderViewModel(
        IDocumentTabService tabService,
        IPdfService pdfService,
        IRecentFilesService recentFilesService,
        ITabCloseService tabCloseService)
    {
        _tabService = tabService;
        _pdfService = pdfService;
        _recentFilesService = recentFilesService;
        _tabCloseService = tabCloseService;
        _tabService.StateChanged += OnSessionStateChanged;
        _tabService.TabsChanged += OnTabsChanged;
        SyncTabItems();
    }

    public bool ShowRecentFiles => !HasDocument && RecentFiles.Count > 0;

    public bool ShowEmptyState => !HasDocument;

    public bool ShowTabBar => TabCount > 1;

    public byte[]? GetCurrentPagePngBytes() => _lastRenderedPng;

    [ObservableProperty]
    public partial BitmapImage? PageImage { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; private set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial string SearchStatus { get; private set; } = string.Empty;

    public bool HasDocument => _tabService.ActiveDocument is not null;

    public string DocumentTitle =>
        _tabService.ActiveFileName ?? "No document open";

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

    public string ZoomLabel => $"{Math.Round(EffectiveZoomScale * 100)}%";

    public double EffectiveZoomScale => ResolveRenderScale();

    public double ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            if (Math.Abs(_viewportWidth - value) < 1)
            {
                return;
            }

            _viewportWidth = Math.Max(200, value);
            if (_tabService.ZoomMode is PdfZoomMode.FitWidth or PdfZoomMode.FitPage)
            {
                _ = RenderCurrentPageAsync();
            }

            OnPropertyChanged(nameof(ZoomLabel));
        }
    }

    public async Task LoadDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        DismissStatus();

        try
        {
            await _tabService.OpenOrActivateTabAsync(path, cancellationToken);
            SelectedTabId = _tabService.ActiveTabId;
            SyncTabItems();
            NotifyDocumentChanged();
            await RenderCurrentPageAsync();
            await RefreshRecentFilesAsync(cancellationToken);
            SetStatus($"Opened {Path.GetFileName(path)}.", InfoBarSeverity.Success);
        }
        catch (PdfiumDependencyException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Open cancelled.", InfoBarSeverity.Informational);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadFilesAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        foreach (var filePath in filePaths)
        {
            await _tabService.OpenOrActivateTabAsync(filePath, cancellationToken);
        }

        SelectedTabId = _tabService.ActiveTabId;
        SyncTabItems();
        NotifyDocumentChanged();
        await RenderCurrentPageAsync();
        await RefreshRecentFilesAsync(cancellationToken);
    }

    public async Task ActivateTabAsync(Guid tabId)
    {
        await _tabService.ActivateTabAsync(tabId);
        SelectedTabId = tabId;
        NotifyDocumentChanged();
        await RenderCurrentPageAsync();
        if (IsThumbnailPanelOpen)
        {
            await EnsureThumbnailsLoadedAsync();
        }
    }

    public async Task<bool> TryCloseTabAsync(Guid tabId)
    {
        if (!await _tabCloseService.TryCloseTabAsync(tabId))
        {
            return false;
        }

        await ApplyTabStateAfterCloseAsync();
        return true;
    }

    public async Task ApplyTabStateAfterCloseAsync()
    {
        SelectedTabId = _tabService.ActiveTabId;
        SyncTabItems();
        NotifyDocumentChanged();
        await RenderCurrentPageAsync();
        if (IsThumbnailPanelOpen)
        {
            await EnsureThumbnailsLoadedAsync();
        }

        await RefreshRecentFilesAsync();
    }

    public void ClosePanels()
    {
        IsThumbnailPanelOpen = false;
        IsSearchPanelOpen = false;
    }

    public async Task EnsureThumbnailsLoadedAsync()
    {
        if (!HasDocument)
        {
            PageThumbnails.Clear();
            return;
        }

        await LoadPageThumbnailsAsync();
    }

    partial void OnIsThumbnailPanelOpenChanged(bool value)
    {
        if (value)
        {
            _ = EnsureThumbnailsLoadedAsync();
        }
    }

    [RelayCommand]
    private void ToggleThumbnailPanel()
    {
        if (IsThumbnailPanelOpen)
        {
            IsThumbnailPanelOpen = false;
            return;
        }

        IsSearchPanelOpen = false;
        IsThumbnailPanelOpen = true;
    }

    [RelayCommand]
    private void ToggleSearchPanel()
    {
        if (IsSearchPanelOpen)
        {
            IsSearchPanelOpen = false;
            return;
        }

        IsThumbnailPanelOpen = false;
        IsSearchPanelOpen = true;
    }

    [RelayCommand]
    private void GoToThumbnailPage(PageThumbnailViewModel? thumbnail)
    {
        if (thumbnail is null)
        {
            return;
        }

        GoToPage(thumbnail.PageIndex);
        ClosePanels();
    }

    public async Task RefreshFromSessionAsync()
    {
        NotifyDocumentChanged();
        await RenderCurrentPageAsync();
    }

    public void GoToPage(int pageIndex)
    {
        _tabService.CurrentPageIndex = pageIndex;
        _ = RenderCurrentPageAsync();
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (!HasDocument)
        {
            return;
        }

        _tabService.CurrentPageIndex -= 1;
        _ = RenderCurrentPageAsync();
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!HasDocument)
        {
            return;
        }

        _tabService.CurrentPageIndex += 1;
        _ = RenderCurrentPageAsync();
    }

    [RelayCommand]
    private void ZoomIn()
    {
        _tabService.ZoomMode = PdfZoomMode.Custom;
        _tabService.ZoomScale *= 1.25;
        NotifyZoomChanged();
        _ = RenderCurrentPageAsync();
    }

    [RelayCommand]
    private void ZoomOut()
    {
        _tabService.ZoomMode = PdfZoomMode.Custom;
        _tabService.ZoomScale /= 1.25;
        NotifyZoomChanged();
        _ = RenderCurrentPageAsync();
    }

    [RelayCommand]
    private void ZoomActualSize()
    {
        _tabService.ZoomMode = PdfZoomMode.ActualSize;
        _tabService.ZoomScale = 96.0 / 72.0;
        NotifyZoomChanged();
        _ = RenderCurrentPageAsync();
    }

    [RelayCommand]
    private void ZoomFitWidth()
    {
        _tabService.ZoomMode = PdfZoomMode.FitWidth;
        NotifyZoomChanged();
        _ = RenderCurrentPageAsync();
    }

    [RelayCommand]
    private void ZoomFitPage()
    {
        _tabService.ZoomMode = PdfZoomMode.FitPage;
        NotifyZoomChanged();
        _ = RenderCurrentPageAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var document = _tabService.ActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchStatus = string.Empty;
            _searchMatches = [];
            _activeSearchMatchIndex = -1;
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            _searchMatches = await _pdfService.SearchTextAsync(document, SearchQuery, MatchCase, token);
            if (_searchMatches.Count == 0)
            {
                _activeSearchMatchIndex = -1;
                SearchStatus = "No matches found.";
                return;
            }

            _activeSearchMatchIndex = 0;
            var match = _searchMatches[0];
            _tabService.CurrentPageIndex = match.PageIndex;
            await RenderCurrentPageAsync();
            SearchStatus = $"Match 1 of {_searchMatches.Count}";
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
    }

    [RelayCommand]
    private async Task NextSearchMatchAsync()
    {
        if (_searchMatches.Count == 0)
        {
            await SearchAsync();
            return;
        }

        _activeSearchMatchIndex = (_activeSearchMatchIndex + 1) % _searchMatches.Count;
        var match = _searchMatches[_activeSearchMatchIndex];
        _tabService.CurrentPageIndex = match.PageIndex;
        await RenderCurrentPageAsync();
        SearchStatus = $"Match {_activeSearchMatchIndex + 1} of {_searchMatches.Count}";
    }

    [RelayCommand]
    private async Task PreviousSearchMatchAsync()
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        _activeSearchMatchIndex = (_activeSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        var match = _searchMatches[_activeSearchMatchIndex];
        _tabService.CurrentPageIndex = match.PageIndex;
        await RenderCurrentPageAsync();
        SearchStatus = $"Match {_activeSearchMatchIndex + 1} of {_searchMatches.Count}";
    }

    [RelayCommand]
    private void OpenOrganize() => Navigation.AppNavigation.RequestWorkspace("organize");

    [RelayCommand]
    private async Task OpenRecentAsync(RecentFileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await LoadDocumentAsync(item.FilePath);
    }

    public void DismissStatus() => IsStatusOpen = false;

    public void Dispose()
    {
        _tabService.StateChanged -= OnSessionStateChanged;
        _tabService.TabsChanged -= OnTabsChanged;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        NotifyDocumentChanged();
        NotifyZoomChanged();
        _ = RenderCurrentPageAsync();
    }

    private void NotifyDocumentChanged()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowRecentFiles));
    }

    public async Task RefreshRecentFilesAsync(CancellationToken cancellationToken = default)
    {
        RecentFiles.Clear();
        foreach (var path in await _recentFilesService.GetRecentFilesAsync(cancellationToken))
        {
            RecentFiles.Add(new RecentFileItemViewModel(path));
        }

        OnPropertyChanged(nameof(ShowRecentFiles));
    }

    private async Task LoadPageThumbnailsAsync()
    {
        PageThumbnails.Clear();
        var document = _tabService.ActiveDocument;
        if (document is null)
        {
            return;
        }

        for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            var bytes = await _pdfService.RenderPageThumbnailAsync(document, pageIndex, 120, 160);
            PageThumbnails.Add(new PageThumbnailViewModel(
                pageIndex,
                await BitmapHelper.CreateBitmapAsync(bytes),
                pageIndex == _tabService.CurrentPageIndex));
        }
    }

    private void NotifyZoomChanged()
    {
        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(EffectiveZoomScale));
    }

    private double ResolveRenderScale()
    {
        var document = _tabService.ActiveDocument;
        if (document is null)
        {
            return 1.0;
        }

        return ZoomScaleCalculator.ResolveScale(
            _tabService.ZoomMode,
            _tabService.ZoomScale,
            _viewportWidth);
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
        var pageIndex = _tabService.CurrentPageIndex;
        var scale = ResolveRenderScale();

        try
        {
            IsBusy = true;
            await Task.Delay(120, token);
            var rendered = await _pdfService.RenderPageAsync(document, pageIndex, scale, token);
            _lastRenderedPng = rendered.PngBytes;
            PageImage = await BitmapHelper.CreateBitmapAsync(rendered.PngBytes);
            UpdateSelectedThumbnail(pageIndex);
            NotifyDocumentChanged();
            NotifyZoomChanged();
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

    private void OnTabsChanged(object? sender, EventArgs e) => SyncTabItems();

    private void UpdateSelectedThumbnail(int pageIndex)
    {
        if (!IsThumbnailPanelOpen || PageThumbnails.Count == 0)
        {
            return;
        }

        foreach (var thumbnail in PageThumbnails)
        {
            thumbnail.IsSelected = thumbnail.PageIndex == pageIndex;
        }
    }

    private void SyncTabItems()
    {
        TabItems.Clear();
        foreach (var tab in _tabService.Tabs)
        {
            TabItems.Add(new DocumentTabItemViewModel(tab.Id, tab.DisplayName));
        }

        TabCount = TabItems.Count;
        SelectedTabId = _tabService.ActiveTabId;
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
