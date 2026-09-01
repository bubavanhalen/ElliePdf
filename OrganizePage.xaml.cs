using ElliePdf.ViewModels;
using ElliePdf.Services;
using ElliePdf.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace ElliePdf.Pages;

public sealed partial class OrganizePage : Page
{
    private readonly AppNavigation _navigation;
    private readonly UiHostContext _uiHost;
    private bool _mergeEventsAttached;

    public OrganizePage(
        DocumentCollectionViewModel viewModel,
        AppNavigation navigation,
        UiHostContext uiHost)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _navigation = navigation;
        _uiHost = uiHost;
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public DocumentCollectionViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_mergeEventsAttached)
        {
            return;
        }

        ViewModel.MergeCompleted += OnMergeCompleted;
        _mergeEventsAttached = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_mergeEventsAttached)
        {
            ViewModel.MergeCompleted -= OnMergeCompleted;
            _mergeEventsAttached = false;
        }
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
        await SaveAsAsync();
    }

    private async void OverwriteButton_Click(object sender, RoutedEventArgs e)
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

        var confirmation = new ContentDialog
        {
            Title = AppResources.Get("Organize_OverwriteConfirmTitle"),
            Content = AppResources.Format("Organize_OverwriteConfirmContent", Path.GetFileName(file.Path)),
            PrimaryButtonText = AppResources.Get("Organize_OverwriteConfirmAction"),
            CloseButtonText = AppResources.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await confirmation.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.OverwriteDocumentsAsync(file.Path);
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => ViewModel.Undo();

    private void RedoButton_Click(object sender, RoutedEventArgs e) => ViewModel.Redo();

    private async void MergeButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = AppResources.Get("Organize_ExportFileName")
        };

        picker.FileTypeChoices.Add(AppResources.Get("Reader_PdfFileType"), [".pdf"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await ViewModel.MergeDocumentsAsync(file.Path);
    }

    private async void BackToDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CancelAsync();
        _navigation.RequestWorkspace("read");
    }

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

    private void MoveLeftButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is DocumentItemViewModel item)
        {
            ViewModel.ReorderPage(item, Math.Max(0, ViewModel.Pages.IndexOf(item) - 1));
        }
    }

    private void MoveRightButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is DocumentItemViewModel item)
        {
            ViewModel.ReorderPage(item, Math.Min(ViewModel.Pages.Count - 1, ViewModel.Pages.IndexOf(item) + 1));
        }
    }

    private void DuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is DocumentItemViewModel item)
        {
            ViewModel.DuplicatePage(item);
        }
    }

    private async void OnMergeCompleted(object? sender, string outputPath)
    {
        var dialog = new ContentDialog
        {
            Title = AppResources.Get("Organize_ExportCompleteTitle"),
            Content = AppResources.Format("Organize_ExportCompleteContent", Path.GetFileName(outputPath)),
            PrimaryButtonText = AppResources.Get("Organize_ExportCompleteOpen"),
            CloseButtonText = AppResources.Get("Organize_ExportCompleteNotNow"),
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await _uiHost.OpenFilesAsync([outputPath]);
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

    private async Task SaveAsAsync()
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = AppResources.Get("Organize_ExportFileName")
        };
        picker.FileTypeChoices.Add(AppResources.Get("Reader_PdfFileType"), [".pdf"]);
        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await ViewModel.SaveDocumentsAsAsync(file.Path);
        }
    }
}
