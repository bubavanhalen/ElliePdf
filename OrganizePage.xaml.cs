using ElliePdf.ViewModels;
using ElliePdf.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace ElliePdf.Pages;

public sealed partial class OrganizePage : Page
{
    public OrganizePage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<DocumentCollectionViewModel>();
        DataContext = ViewModel;
        ViewModel.MergeCompleted += OnMergeCompleted;
        Unloaded += OnUnloaded;
    }

    public DocumentCollectionViewModel ViewModel { get; }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.MergeCompleted -= OnMergeCompleted;
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var paths = await PickPdfFilesAsync();
        if (paths.Count == 0)
        {
            return;
        }

        await ViewModel.ImportDocumentsAsync(paths, append: false);
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var paths = await PickPdfFilesAsync();
        if (paths.Count == 0)
        {
            return;
        }

        await ViewModel.ImportDocumentsAsync(paths, append: true);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveDocumentsAsync();
    }

    private async void MergeButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "ElliePdf-Export"
        };

        picker.FileTypeChoices.Add("PDF Document", [".pdf"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await ViewModel.MergeDocumentsAsync(file.Path);
    }

    private void BackToDocumentButton_Click(object sender, RoutedEventArgs e) =>
        AppNavigation.RequestWorkspace("read");

    private async void ThumbnailGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DocumentItemViewModel item)
        {
            await ViewModel.OpenInReaderAsync(item);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is DocumentItemViewModel item)
        {
            await ViewModel.DeletePageAsync(item);
        }
    }

    private async void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is DocumentItemViewModel item)
        {
            await ViewModel.RotatePageAsync(item);
        }
    }

    private async void OnMergeCompleted(object? sender, string outputPath)
    {
        var dialog = new ContentDialog
        {
            Title = "Export complete",
            Content = $"Open '{Path.GetFileName(outputPath)}' in the reader?",
            PrimaryButtonText = "Open",
            CloseButtonText = "Not now",
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && App.Window is MainWindow mainWindow)
        {
            await mainWindow.OpenFilesAsync([outputPath]);
        }
    }

    private async Task<IReadOnlyList<string>> PickPdfFilesAsync()
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add(".pdf");

        var files = await picker.PickMultipleFilesAsync();
        return files.Select(file => file.Path).ToArray();
    }
}
