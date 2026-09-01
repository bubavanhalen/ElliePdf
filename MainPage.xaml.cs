using ElliePdf.Navigation;
using ElliePdf.Pages;
using ElliePdf.Services;
using ElliePdf.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf;

public sealed partial class MainPage : Page
{
    private readonly ReaderPage _readerPage;
    private readonly OrganizePage _organizePage;
    private readonly SettingsPage _settingsPage;
    private readonly AppNavigation _navigation;
    private readonly IUserSettingsService _settings;
    private readonly DocumentCollectionViewModel _documentCollection;
    private readonly UiHostContext _uiHost;
    private readonly Stack<string> _workspaceHistory = [];
    private string _currentWorkspace = "read";
    private bool _isSelectingWorkspace;

    public FlowDirection UiFlowDirection => System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
        ? FlowDirection.RightToLeft
        : FlowDirection.LeftToRight;

    public MainPage(
        ReaderPage readerPage,
        OrganizePage organizePage,
        SettingsPage settingsPage,
        AppNavigation navigation,
        IUserSettingsService settings,
        DocumentCollectionViewModel documentCollection,
        UiHostContext uiHost)
    {
        _readerPage = readerPage;
        _organizePage = organizePage;
        _settingsPage = settingsPage;
        _navigation = navigation;
        _settings = settings;
        _documentCollection = documentCollection;
        _uiHost = uiHost;

        InitializeComponent();
        OrganizeNavItem.Visibility = IsLabsEnabled() ? Visibility.Visible : Visibility.Collapsed;
        Loaded += MainPage_Loaded;
        _navigation.WorkspaceRequested += OnWorkspaceRequested;
        _navigation.ReaderPageRequested += OnReaderPageRequested;
        Unloaded += OnUnloaded;

        _isSelectingWorkspace = true;
        ContentFrame.Content = _readerPage;
        if (NavView.MenuItems[0] is NavigationViewItem readItem)
        {
            NavView.SelectedItem = readItem;
        }
        _isSelectingWorkspace = false;
        SyncShellButtons();
    }

    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        SelectWorkspace("read");
        await _readerPage.LoadFilesAsync(filePaths);

        if (filePaths.Count > 1 && IsLabsEnabled())
        {
            await _documentCollection.ImportDocumentsAsync(filePaths);
            SelectWorkspace("organize");
        }
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        _uiHost.SetTitleBar(WindowDragSurface);
        UpdateTitleBarDragRegion();
        SyncShellButtons();
    }

    private void MainPage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarDragRegion();

    private void UpdateTitleBarDragRegion()
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        // The tab strip reserves 108 epx before this region. Keep the draggable
        // element inside that reservation so caption hit testing never steals a tab.
        WindowDragSurface.Width = Math.Clamp(48 + (ActualWidth - 500) * 0.12, 48, 96);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _navigation.WorkspaceRequested -= OnWorkspaceRequested;
        _navigation.ReaderPageRequested -= OnReaderPageRequested;
    }

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceHistory.TryPop(out var workspace))
        {
            SelectWorkspace(workspace, addToHistory: false);
        }
    }

    private void SyncShellButtons() =>
        BackButton.Visibility = _workspaceHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnWorkspaceRequested(string tag) => SelectWorkspace(tag);

    private void OnReaderPageRequested(int pageIndex)
    {
        SelectWorkspace("read");
        _readerPage.GoToPage(pageIndex);
    }

    internal ReaderPage BenchmarkReaderPage => _readerPage;

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isSelectingWorkspace)
        {
            return;
        }

        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            if (tag == "read")
            {
                sender.IsPaneOpen = false;
            }

            SelectWorkspace(tag);
        }
    }

    private void SelectWorkspace(string tag, bool addToHistory = true)
    {
        if (tag == "organize" && !IsLabsEnabled())
        {
            tag = "read";
        }

        Page target;
        int menuIndex;
        switch (tag)
        {
            case "read":
                target = _readerPage;
                menuIndex = 0;
                NavView.IsPaneOpen = false;
                break;
            case "organize":
                target = _organizePage;
                menuIndex = 1;
                break;
            case "settings":
                target = _settingsPage;
                menuIndex = 2;
                break;
            default:
                return;
        }

        _isSelectingWorkspace = true;
        try
        {
            if (NavView.MenuItems[menuIndex] is NavigationViewItem item)
            {
                NavView.SelectedItem = item;
            }

            if (!ReferenceEquals(ContentFrame.Content, target))
            {
                if (addToHistory && !string.Equals(_currentWorkspace, tag, StringComparison.Ordinal))
                {
                    _workspaceHistory.Push(_currentWorkspace);
                }

                ContentFrame.Content = target;
                _currentWorkspace = tag;
            }
        }
        finally
        {
            _isSelectingWorkspace = false;
        }

        SyncShellButtons();
    }

    private bool IsLabsEnabled() => _settings.Settings.EnableLabs;
}
