using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics.Printing;
using Windows.System;

namespace ElliePdf.Pages;

public sealed partial class ReaderPage : Page
{
    private bool _isSyncingTabs;

    private PrintDocument? _printDocument;

    public ReaderPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<ReaderViewModel>();
        DataContext = ViewModel;

        PageViewer.ViewportWidthChanged += OnViewportWidthChanged;
        PageViewer.ZoomInRequested += (_, _) => ViewModel.ZoomInCommand.Execute(null);
        PageViewer.ZoomOutRequested += (_, _) => ViewModel.ZoomOutCommand.Execute(null);
        PageViewer.PagePointerPressed += (_, _) => ViewModel.ClosePanels();
        ViewModel.TabItems.CollectionChanged += OnTabItemsChanged;
        Loaded += OnLoaded;
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
        }
    }

    private void OnViewportWidthChanged(object? sender, double width) => ViewModel.ViewportWidth = width;

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

    private async void RecentFiles_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentFileItemViewModel item)
        {
            ViewModel.ClosePanels();
            await ViewModel.OpenRecentCommand.ExecuteAsync(item);
            SyncTabViewItems();
        }
    }

    private async void PrintButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.GetCurrentPagePngBytes() is null || PrintTarget.Source is not BitmapImage)
        {
            return;
        }

        if (!PrintManager.IsSupported())
        {
            return;
        }

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
        }
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

        _printDocument.SetPreviewPageCount(1, PreviewPageCountType.Final);
    }

    private void OnPrintGetPreviewPage(object? sender, GetPreviewPageEventArgs e)
    {
        if (_printDocument is null || PrintTarget.Source is not BitmapImage image)
        {
            return;
        }

        var page = CreatePrintPage(image);
        _printDocument.SetPreviewPage(e.PageNumber, page);
    }

    private void OnPrintAddPages(object? sender, AddPagesEventArgs e)
    {
        if (_printDocument is null || PrintTarget.Source is not BitmapImage image)
        {
            return;
        }

        _printDocument.AddPage(CreatePrintPage(image));
        _printDocument.AddPagesComplete();
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
