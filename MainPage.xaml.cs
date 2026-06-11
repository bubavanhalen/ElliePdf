using ElliePdf.Navigation;
using ElliePdf.Pages;
using ElliePdf.Services;
using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf;

public sealed partial class MainPage : Page
{
    private ReaderPage? _readerPage;

    public MainPage()
    {
        InitializeComponent();
        AppNavigation.WorkspaceRequested += OnWorkspaceRequested;
        AppNavigation.ReaderPageRequested += OnReaderPageRequested;
        Unloaded += OnUnloaded;

        ContentFrame.Navigate(typeof(ReaderPage));
        if (NavView.MenuItems[0] is NavigationViewItem readItem)
        {
            NavView.SelectedItem = readItem;
        }
    }

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

    private void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AppNavigation.WorkspaceRequested -= OnWorkspaceRequested;
        AppNavigation.ReaderPageRequested -= OnReaderPageRequested;
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

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            if (tag == "read")
            {
                sender.IsPaneOpen = false;
            }

            SelectWorkspace(tag);
        }
    }

    private void SelectWorkspace(string tag)
    {
        if (tag == "read")
        {
            NavView.IsPaneOpen = false;

            if (NavView.MenuItems[0] is NavigationViewItem readItem)
            {
                NavView.SelectedItem = readItem;
            }

            if (ContentFrame.CurrentSourcePageType != typeof(ReaderPage))
            {
                ContentFrame.Navigate(typeof(ReaderPage));
                _readerPage = (ReaderPage)ContentFrame.Content;
            }
        }
        else if (tag == "organize")
        {
            if (NavView.MenuItems[1] is NavigationViewItem organizeItem)
            {
                NavView.SelectedItem = organizeItem;
            }

            if (ContentFrame.CurrentSourcePageType != typeof(OrganizePage))
            {
                ContentFrame.Navigate(typeof(OrganizePage));
            }
        }
    }
}
