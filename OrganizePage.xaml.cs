using ElliePdf.ViewModels;
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
        ViewModel = App.AppHost.Services.GetRequiredService<DocumentCollectionViewModel>();
        DataContext = ViewModel;
    }

    public DocumentCollectionViewModel ViewModel { get; }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add(".pdf");

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        await ViewModel.ImportDocumentsAsync(files.Select(file => file.Path).ToArray());
    }

    private async void MergeButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "ElliePdf-Merged"
        };

        picker.FileTypeChoices.Add("PDF Document", [".pdf"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await ViewModel.MergeDocumentsAsync(file.Path);
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
}
