using ElliePdf.Navigation;
using ElliePdf.Pages;
using ElliePdf.Services;
using ElliePdf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ElliePdf;

public sealed partial class MainPage : Page
{
    private ReaderPage? _readerPage;

    public MainPage()
    {
        InitializeComponent();
        Loaded += MainPage_Loaded;
        ContentFrame.Navigated += ContentFrame_Navigated;
        AppNavigation.WorkspaceRequested += OnWorkspaceRequested;
        AppNavigation.ReaderPageRequested += OnReaderPageRequested;
        Unloaded += OnUnloaded;

        ContentFrame.Navigate(typeof(ReaderPage));
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

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Window.SetTitleBar(WindowDragSurface);
        SyncShellButtons();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigated -= ContentFrame_Navigated;
        AppNavigation.WorkspaceRequested -= OnWorkspaceRequested;
        AppNavigation.ReaderPageRequested -= OnReaderPageRequested;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        SelectWorkspace("read");
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) =>
        SyncShellButtons();

    private void SyncShellButtons() =>
        BackButton.Visibility = ContentFrame.CurrentSourcePageType == typeof(ReaderPage)
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void OrganizeButton_Click(object sender, RoutedEventArgs e) =>
        SelectWorkspace("organize");

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        SelectWorkspace("settings");

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
        if (tag == "read")
        {
            if (ContentFrame.CurrentSourcePageType != typeof(ReaderPage))
            {
                ContentFrame.Navigate(typeof(ReaderPage));
                _readerPage = (ReaderPage)ContentFrame.Content;
            }
        }
        else if (tag == "organize")
        {
            if (ContentFrame.CurrentSourcePageType != typeof(OrganizePage))
            {
                ContentFrame.Navigate(typeof(OrganizePage));
            }
        }
        else if (tag == "settings")
        {
            if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
            {
                ContentFrame.Navigate(typeof(SettingsPage));
            }
        }
    }
}
