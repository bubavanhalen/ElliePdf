using ElliePdf.Navigation;
using ElliePdf.Pages;
using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace ElliePdf;

public sealed partial class MainPage : Page
{
    private ReaderPage? _readerPage;
    private bool _isSyncingTabs;
    private bool _isSyncingWorkspace;

    public MainPage()
    {
        Reader = App.Services.GetRequiredService<ReaderViewModel>();
        InitializeComponent();
        Loaded += MainPage_Loaded;
        ContentFrame.Navigated += ContentFrame_Navigated;
        Reader.TabItems.CollectionChanged += OnTabItemsChanged;
        Reader.PropertyChanged += OnReaderPropertyChanged;
        AppNavigation.WorkspaceRequested += OnWorkspaceRequested;
        AppNavigation.ReaderPageRequested += OnReaderPageRequested;
        Unloaded += OnUnloaded;

        ContentFrame.Navigate(typeof(ReaderPage));
    }

    public ReaderViewModel Reader { get; }

    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        SelectWorkspace("read");

        var readerPage = EnsureReaderPage();
        await readerPage.LoadFilesAsync(filePaths);

        if (filePaths.Count > 1)
        {
            await App.Services
                .GetRequiredService<DocumentCollectionViewModel>()
                .ImportDocumentsAsync(filePaths);
            SelectWorkspace("organize");
        }
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Window.SetTitleBar(DragRegion);
        SyncShellButtons();
        SyncTabViewItems();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigated -= ContentFrame_Navigated;
        Reader.TabItems.CollectionChanged -= OnTabItemsChanged;
        Reader.PropertyChanged -= OnReaderPropertyChanged;
        AppNavigation.WorkspaceRequested -= OnWorkspaceRequested;
        AppNavigation.ReaderPageRequested -= OnReaderPageRequested;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SelectWorkspace("settings");

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) =>
        SyncShellButtons();

    private void SyncShellButtons()
    {
        BackButton.Visibility = ContentFrame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        SyncWorkspaceSwitcher();
    }

    private void SyncWorkspaceSwitcher()
    {
        _isSyncingWorkspace = true;
        try
        {
            WorkspaceSwitcher.SelectedItem = ContentFrame.CurrentSourcePageType switch
            {
                var type when type == typeof(ReaderPage) => ReadWorkspaceItem,
                var type when type == typeof(OrganizePage) => OrganizeWorkspaceItem,
                _ => null
            };
        }
        finally
        {
            _isSyncingWorkspace = false;
        }
    }

    private void WorkspaceSwitcher_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_isSyncingWorkspace || sender.SelectedItem?.Tag is not string tag)
        {
            return;
        }

        SelectWorkspace(tag);
    }

    private void OnWorkspaceRequested(string tag) => SelectWorkspace(tag);

    private void OnReaderPageRequested(int pageIndex)
    {
        if (EnsureReaderPage() is { } readerPage)
        {
            readerPage.GoToPage(pageIndex);
        }
    }

    private ReaderPage EnsureReaderPage()
    {
        if (_readerPage is not null)
        {
            return _readerPage;
        }

        if (ContentFrame.Content is ReaderPage currentReader)
        {
            _readerPage = currentReader;
            return _readerPage;
        }

        ContentFrame.Navigate(typeof(ReaderPage));
        _readerPage = (ReaderPage)ContentFrame.Content;
        return _readerPage;
    }

    private void SelectWorkspace(string tag)
    {
        var targetType = tag switch
        {
            "organize" => typeof(OrganizePage),
            "settings" => typeof(SettingsPage),
            _ => typeof(ReaderPage)
        };

        if (ContentFrame.CurrentSourcePageType != targetType)
        {
            ContentFrame.Navigate(targetType);
            if (targetType == typeof(ReaderPage))
            {
                _readerPage = (ReaderPage)ContentFrame.Content;
            }
        }

        SyncShellButtons();
    }

    // ═══════════ Document tab strip ═══════════

    private void OnReaderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReaderViewModel.SelectedTabId) or nameof(ReaderViewModel.TabCount))
        {
            SyncTabViewItems();
        }
    }

    private void OnTabItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_isSyncingTabs)
        {
            SyncTabViewItems();
        }
    }

    private void SyncTabViewItems()
    {
        _isSyncingTabs = true;
        try
        {
            DocumentTabs.TabItems.Clear();

            foreach (var tab in Reader.TabItems)
            {
                DocumentTabs.TabItems.Add(new TabViewItem
                {
                    Header = tab.Title,
                    IsClosable = true,
                    Tag = tab.TabId,
                    IconSource = new FontIconSource { Glyph = "\uE8A5", FontSize = 14 }
                });
            }

            if (Reader.SelectedTabId is Guid selectedId)
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

    private async void DocumentTabs_AddTabButtonClick(TabView sender, object args)
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

        SelectWorkspace("read");
        Reader.ClosePanels();
        await Reader.LoadDocumentAsync(file.Path);
    }

    private async void DocumentTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab?.Tag is not Guid tabId)
        {
            return;
        }

        var tabItem = args.Tab;
        if (!await Reader.TryCloseTabAsync(tabId))
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

        SelectWorkspace("read");
        Reader.ClosePanels();
        await Reader.ActivateTabAsync(tabId);
    }
}
