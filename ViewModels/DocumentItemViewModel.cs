using CommunityToolkit.Mvvm.ComponentModel;
using ElliePdf.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ElliePdf.ViewModels;

public sealed class DocumentItemViewModel : ObservableObject
{
    private string _filePath = string.Empty;
    private int _pageIndex;
    private BitmapImage? _thumbnail;
    private string _displayName = string.Empty;
    private string _sourceLabel = string.Empty;

    public DocumentItemViewModel(PdfDocumentSession document, string filePath, int pageIndex)
    {
        Document = document;
        FilePath = filePath;
        PageIndex = pageIndex;
        SourceLabel = Path.GetFileName(filePath);
        DisplayName = $"Page {pageIndex + 1}";
    }

    public PdfDocumentSession Document { get; }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public int PageIndex
    {
        get => _pageIndex;
        set => SetProperty(ref _pageIndex, value);
    }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string SourceLabel
    {
        get => _sourceLabel;
        set => SetProperty(ref _sourceLabel, value);
    }
}
