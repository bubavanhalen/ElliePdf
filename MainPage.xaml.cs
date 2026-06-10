using Microsoft.UI.Xaml.Controls;
using ElliePdf.ViewModels;
using ElliePdf.Pages;
using Microsoft.Extensions.DependencyInjection;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ElliePdf;

/// <summary>
/// The main content page displayed inside the application window./// </summary>
public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        InitializeComponent();
        // Resolve ViewModel from the application host's service provider so DI can be used.
        ViewModel = App.AppHost.Services.GetRequiredService<MainPageViewModel>();
        DataContext = ViewModel;
        // Navigate to the default workspace on load.
        ContentFrame.Navigate(typeof(OrganizePage));
    }

    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        SelectWorkspace("organize");
        await App.AppHost.Services
            .GetRequiredService<DocumentCollectionViewModel>()
            .ImportDocumentsAsync(filePaths);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            SelectWorkspace(tag);
        }
    }

    private void SelectWorkspace(string tag)
    {
        if (tag == "organize")
        {
            if (NavView.MenuItems[0] is NavigationViewItem organizeItem)
            {
                NavView.SelectedItem = organizeItem;
            }

            if (ContentFrame.CurrentSourcePageType != typeof(OrganizePage))
            {
                ContentFrame.Navigate(typeof(OrganizePage));
            }
        }
        else if (tag == "edit")
        {
            if (NavView.MenuItems[1] is NavigationViewItem editItem)
            {
                NavView.SelectedItem = editItem;
            }

            if (ContentFrame.CurrentSourcePageType != typeof(EditPage))
            {
                ContentFrame.Navigate(typeof(EditPage));
            }
        }
    }
}
